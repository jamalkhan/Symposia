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
/// be paid. Each tick marks the minute as up, then advances the last finished epoch through
/// Measured → Signed → Submitted, persisting each step in <c>epoch_report</c>: factors are measured once, a
/// restart resends the same signed bytes, and a report that already landed on chain is never sent again.
/// A window that closes first is lost for good (Terminal), and so is any epoch the service wasn't Active in
/// (Skipped). Holds the history sweep back to the start of the oldest epoch not yet measured.
/// </summary>
public sealed class ServiceMetricsReporter(
    HistoryStore store, HistoryRecorder recorder, IServiceMetricsChain chain, IServiceMetricsRelay relay, Func<ulong, IMetricAttestor> attestorFor,
    string node, IOptions<ServiceMetricsOptions> options, ILogger<ServiceMetricsReporter> logger, TimeProvider? time = null) : BackgroundService
{
    public const byte BlobGateway = 8;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IMetricAttestor? _attestor;
    private ulong _lastWarnedEpoch = ulong.MaxValue;

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
                logger.LogWarning("Service metrics: tick failed, retrying: {Reason}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass; public for tests.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        store.RecordLiveness(_time.GetUtcNow());
        _attestor ??= attestorFor(await chain.ChainIdAsync(ct));

        var current = await chain.CurrentEpochAsync(ct);
        if (current == 0)
        {
            recorder.HoldFrom = DateTimeOffset.FromUnixTimeSeconds(await chain.EpochStartAsync(0, ct));
            return;
        }

        var epoch = current - 1;
        var row = await AdvanceAsync(epoch, ct);
        var holdEpoch = row.State is EpochReportState.Measured or EpochReportState.Signed ? epoch : current;
        recorder.HoldFrom = DateTimeOffset.FromUnixTimeMilliseconds(holdEpoch == epoch ? row.StartMs : row.EndMs);
    }

    private async Task<EpochReportRow> AdvanceAsync(ulong epoch, CancellationToken ct)
    {
        var row = store.GetEpochReport(epoch);
        if (row is null)
        {
            var startMs = await chain.EpochStartAsync(epoch, ct) * 1000;
            var endMs = await chain.EpochStartAsync(epoch + 1, ct) * 1000;
            var factors = store.Aggregate(startMs, endMs);
            var state = EpochReportState.Measured;
            string? error = null;
            if (!await chain.AcceptsReportsAsync(epoch, ct))
            {
                (state, error) = (EpochReportState.Terminal, "FinalityWindowElapsed");
                logger.LogWarning("Service metrics: epoch {Epoch}'s report window elapsed before it was measured; no report.", epoch);
            }
            else if (!await chain.IsActiveAsync(node, BlobGateway, ct))
            {
                (state, error) = (EpochReportState.Skipped, "Blob Gateway is not Active");
                logger.LogDebug("Service metrics: Blob Gateway not Active in epoch {Epoch}; nothing to report.", epoch);
            }
            else
            {
                logger.LogInformation("Service metrics: epoch {Epoch} measured: uptime {Uptime} bps, success {Success} bps, p95 {P95} ms, {Bytes} bytes over {Requests} requests.",
                    epoch, factors.UptimeBps, factors.SuccessBps, factors.P95Ms, factors.BytesServed, factors.Requests);
            }

            row = Save(new EpochReportRow(epoch, startMs, endMs, factors, state, null, null, error, 0, 0));
        }

        if (row.State is not (EpochReportState.Measured or EpochReportState.Signed))
        {
            return row;
        }

        var report = ReportOf(row);
        if (row.State == EpochReportState.Measured)
        {
            var attestation = await _attestor!.AttestAsync(report, ct);
            row = Save(row with { State = EpochReportState.Signed, Signature = attestation.Signature });
        }

        // A crash after the transaction mined but before the local write must not send it twice.
        if (await chain.SubmittedAsync(epoch, node, BlobGateway, ct))
        {
            logger.LogInformation("Service metrics: epoch {Epoch}'s report is already on chain.", epoch);
            return Save(row with { State = EpochReportState.Submitted, LastError = null });
        }

        if (!await chain.AcceptsReportsAsync(epoch, ct))
        {
            logger.LogWarning("Service metrics: epoch {Epoch}'s report window elapsed before the report landed ({Error}).", epoch, row.LastError ?? "no attempt");
            return Save(row with { State = EpochReportState.Terminal, LastError = "FinalityWindowElapsed" });
        }

        RelayOutcome outcome;
        try
        {
            outcome = await relay.SubmitAsync(Wire.Request.From(report, new MetricAttestation(row.Signature!, "")), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            outcome = new RelayOutcome(false, false, null, ex.Message);
        }

        if (outcome.Submitted)
        {
            logger.LogInformation("Service metrics: epoch {Epoch} reported in {Tx}.", epoch, outcome.TransactionHash);
            return Save(row with { State = EpochReportState.Submitted, TxHash = outcome.TransactionHash, LastError = null, Attempts = row.Attempts + 1 });
        }

        if (outcome.Terminal)
        {
            logger.LogWarning("Service metrics: epoch {Epoch}'s report was refused: {Error}.", epoch, outcome.Error);
            return Save(row with { State = EpochReportState.Terminal, TxHash = outcome.TransactionHash, LastError = outcome.Error, Attempts = row.Attempts + 1 });
        }

        if (_lastWarnedEpoch != epoch)
        {
            _lastWarnedEpoch = epoch;
            logger.LogWarning("Service metrics: epoch {Epoch}'s report didn't go through, retrying until the window closes: {Error}.", epoch, outcome.Error);
        }

        return Save(row with { TxHash = outcome.TransactionHash ?? row.TxHash, LastError = outcome.Error, Attempts = row.Attempts + 1 });
    }

    private MetricReport ReportOf(EpochReportRow row) => new(options.Value.RewardDistributorAddress, node, BlobGateway, row.Epoch,
        [row.Factors.UptimeBps, row.Factors.SuccessBps, row.Factors.P95Ms, new BigInteger(row.Factors.BytesServed)], row.Factors.UptimeBps);

    private EpochReportRow Save(EpochReportRow row)
    {
        row = row with { UpdatedMs = _time.GetUtcNow().ToUnixTimeMilliseconds() };
        store.SaveEpochReport(row);
        return row;
    }
}
