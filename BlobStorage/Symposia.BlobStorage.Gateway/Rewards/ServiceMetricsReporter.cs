using System.Numerics;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.History;
using Symposia.Network.Node.Rewards;
using Wire = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire;

namespace Symposia.BlobStorage.Gateway.Rewards;

/// <summary>Where the reporter reads the chain and which key signs (#221). Set by the Node Host on LocalDev only.</summary>
public sealed class ServiceMetricsOptions
{
    public bool Enabled { get; set; }

    public string RpcUrl { get; set; } = "";

    public string RewardDistributorAddress { get; set; } = "";

    public string EpochControllerAddress { get; set; } = "";

    public string StakingRegistryAddress { get; set; } = "";

    /// <summary>The LocalDev metric signer (the deployer key). Never set outside LocalDev.</summary>
    public string SignerPrivateKey { get; set; } = "";

    public int PollSeconds { get; set; } = 15;
}

/// <summary>
/// Reports this gateway's Blob Gateway factors for every finished epoch (#221), so a staked, Active gateway can
/// be paid. Each tick marks the minute as up, then advances each of the last <see cref="Lookback"/> finished
/// epochs through Measured → Signed → Submitted, persisting each step in <c>epoch_report</c> (keyed by
/// distributor, so a reset chain starts over): factors are measured once, <see cref="MeasureGrace"/> after the
/// epoch ends so the history writer has caught up; a restart resends the same signed bytes; a report already on
/// chain is never sent again. A window that closes first is lost for good (Terminal). An epoch the service
/// wasn't Active for is Skipped, rechecked while its window is open. Holds the history sweep back to the start
/// of the oldest epoch not yet measured.
/// </summary>
public sealed class ServiceMetricsReporter(
    HistoryStore store, HistoryRecorder recorder, IServiceMetricsChain chain, IServiceMetricsRelay relay, Func<ulong, IMetricAttestor> attestorFor,
    string node, IOptions<ServiceMetricsOptions> options, ILogger<ServiceMetricsReporter> logger, TimeProvider? time = null) : BackgroundService
{
    public const byte BlobGateway = 8;

    /// <summary>Finished epochs looked at per tick; anything older is far past its finality window.</summary>
    public const int Lookback = 3;

    /// <summary>Wait after an epoch ends before measuring it: the history writer batches and backs off up to 30 s.</summary>
    public static readonly TimeSpan MeasureGrace = TimeSpan.FromSeconds(45);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly string _distributor = options.Value.RewardDistributorAddress;
    private readonly HashSet<ulong> _warned = [];
    private IMetricAttestor? _attestor;

    /// <summary>
    /// Checks the chain before the gateway serves (#221 AC6): off LocalDev the self-signer throws and the host
    /// doesn't start. An unreachable RPC isn't a reason to refuse: the first tick checks again.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            _attestor = attestorFor(await chain.ChainIdAsync(timeout.Token));
        }
        catch (Exception ex) when (ex is not MetricSignerConfigurationException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Service metrics: couldn't read the chain ID at startup, checking on the first pass: {Reason}", ex.Message);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)), _time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (MetricSignerConfigurationException)
            {
                throw; // refusing to self-sign off LocalDev stops the gateway: loudly, not one warning a tick
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Service metrics: pass failed, retrying: {Reason}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass; public for tests.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        try
        {
            store.RecordLiveness(_time.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogWarning("Service metrics: couldn't record liveness: {Reason}", ex.Message);
        }

        _attestor ??= attestorFor(await chain.ChainIdAsync(ct));

        var current = await chain.CurrentEpochAsync(ct);
        var hold = DateTimeOffset.FromUnixTimeSeconds(await chain.EpochStartAsync(current, ct));
        for (var epoch = current > Lookback ? current - Lookback : 0; epoch < current; epoch++)
        {
            if (await AdvanceAsync(epoch, ct) is { } pendingFrom && pendingFrom < hold)
            {
                hold = pendingFrom;
            }
        }

        recorder.HoldFrom = hold;
    }

    /// <summary>Moves one epoch's report on; returns the epoch's start while its history is still needed.</summary>
    private async Task<DateTimeOffset?> AdvanceAsync(ulong epoch, CancellationToken ct)
    {
        var row = store.GetEpochReport(_distributor, epoch);
        if (row is { State: EpochReportState.Submitted or EpochReportState.Terminal })
        {
            return null;
        }

        if (row is null)
        {
            var startMs = await chain.EpochStartAsync(epoch, ct) * 1000;
            var endMs = await chain.EpochStartAsync(epoch + 1, ct) * 1000;
            if (!await chain.AcceptsReportsAsync(epoch, ct))
            {
                logger.LogWarning("Service metrics: epoch {Epoch}'s report window elapsed before it was measured; no report.", epoch);
                Save(new EpochReportRow(_distributor, epoch, startMs, endMs, new EpochFactors(0, 0, 0, 0, 0), EpochReportState.Terminal,
                    null, null, "FinalityWindowElapsed", 0, 0));
                return null;
            }

            if (_time.GetUtcNow() < DateTimeOffset.FromUnixTimeMilliseconds(endMs) + MeasureGrace)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(startMs);
            }

            var factors = store.Aggregate(startMs, endMs);
            logger.LogInformation("Service metrics: epoch {Epoch} measured: uptime {Uptime} bps, success {Success} bps, p95 {P95} ms, {Bytes} bytes over {Requests} requests.",
                epoch, factors.UptimeBps, factors.SuccessBps, factors.P95Ms, factors.BytesServed, factors.Requests);
            row = Save(new EpochReportRow(_distributor, epoch, startMs, endMs, factors, EpochReportState.Measured, null, null, null, 0, 0));
        }

        // From here the factors are persisted, so the history rows are no longer needed.
        if (!await chain.AcceptsReportsAsync(epoch, ct))
        {
            if (row.State != EpochReportState.Skipped)
            {
                logger.LogWarning("Service metrics: epoch {Epoch}'s report window elapsed before the report landed ({Error}).", epoch, row.LastError ?? "no attempt");
            }

            Save(row with { State = EpochReportState.Terminal, LastError = row.State == EpochReportState.Skipped ? row.LastError : "FinalityWindowElapsed" });
            return null;
        }

        if (row.State is EpochReportState.Measured or EpochReportState.Skipped)
        {
            // Rechecked while the window is open: a status read just after the boundary can lag a fresh activation.
            if (!await chain.IsActiveAsync(node, BlobGateway, ct))
            {
                if (row.State != EpochReportState.Skipped)
                {
                    logger.LogDebug("Service metrics: Blob Gateway not Active for epoch {Epoch}; nothing to report.", epoch);
                    Save(row with { State = EpochReportState.Skipped, LastError = "Blob Gateway is not Active" });
                }

                return null;
            }

            var attestation = await _attestor!.AttestAsync(ReportOf(row), ct);
            row = Save(row with { State = EpochReportState.Signed, Signature = attestation.Signature, LastError = null });
        }

        // A crash after the transaction mined but before the local write must not send it twice.
        if (await chain.SubmittedAsync(epoch, node, BlobGateway, ct))
        {
            logger.LogInformation("Service metrics: epoch {Epoch}'s report is already on chain.", epoch);
            Save(row with { State = EpochReportState.Submitted, LastError = null });
            return null;
        }

        RelayOutcome outcome;
        try
        {
            outcome = await relay.SubmitAsync(Wire.Request.From(ReportOf(row), new MetricAttestation(row.Signature!, "")), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            outcome = new RelayOutcome(false, false, null, ex.Message);
        }

        if (outcome.Submitted)
        {
            logger.LogInformation("Service metrics: epoch {Epoch} reported in {Tx}.", epoch, outcome.TransactionHash ?? "an earlier transaction");
            Save(row with { State = EpochReportState.Submitted, TxHash = outcome.TransactionHash ?? row.TxHash, LastError = null, Attempts = row.Attempts + 1 });
        }
        else if (outcome.Terminal)
        {
            logger.LogWarning("Service metrics: epoch {Epoch}'s report was refused: {Error}.", epoch, outcome.Error);
            Save(row with { State = EpochReportState.Terminal, TxHash = outcome.TransactionHash, LastError = outcome.Error, Attempts = row.Attempts + 1 });
        }
        else
        {
            if (_warned.Add(epoch))
            {
                logger.LogWarning("Service metrics: epoch {Epoch}'s report didn't go through, retrying until the window closes: {Error}.", epoch, outcome.Error);
            }

            Save(row with { TxHash = outcome.TransactionHash ?? row.TxHash, LastError = outcome.Error, Attempts = row.Attempts + 1 });
        }

        return null;
    }

    private MetricReport ReportOf(EpochReportRow row) => new(_distributor, node, BlobGateway, row.Epoch,
        [row.Factors.UptimeBps, row.Factors.SuccessBps, row.Factors.P95Ms, new BigInteger(row.Factors.BytesServed)], row.Factors.UptimeBps);

    private EpochReportRow Save(EpochReportRow row)
    {
        row = row with { UpdatedMs = _time.GetUtcNow().ToUnixTimeMilliseconds() };
        store.SaveEpochReport(row);
        return row;
    }
}
