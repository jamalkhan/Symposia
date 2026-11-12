using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.History;

namespace Symposia.BlobStorage.Gateway.Rewards;

/// <summary>
/// Measures this gateway's own factors for every finished epoch (#221), for its signed bytes-served figure and its
/// diagnostics. It never signs or submits a metric report: the Verifier role measures gateways independently and
/// is the only attestor (#225). Each tick marks the minute as up, then measures each of the last
/// <see cref="Lookback"/> finished epochs once, <see cref="MeasureGrace"/> after it ends so batched history has
/// landed, and holds the history sweep back to the start of the oldest epoch not yet measured.
/// </summary>
public sealed class EpochFactorRecorder(
    HistoryStore store, HistoryRecorder recorder, IServiceMetricsChain chain, IOptions<ServiceMetricsOptions> options,
    ILogger<EpochFactorRecorder> logger, TimeProvider? time = null) : BackgroundService
{
    /// <summary>Finished epochs looked at per tick; anything older is far past its finality window.</summary>
    public const int Lookback = 3;

    /// <summary>Wait after an epoch ends before measuring it: the history writer batches and backs off up to 30 s.</summary>
    public static readonly TimeSpan MeasureGrace = TimeSpan.FromSeconds(45);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly string _distributor = options.Value.RewardDistributorAddress;

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
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Epoch measurement: pass failed, retrying: {Reason}", ex.Message);
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
            logger.LogWarning("Epoch measurement: couldn't record liveness: {Reason}", ex.Message);
        }

        var current = await chain.CurrentEpochAsync(ct);
        var hold = DateTimeOffset.FromUnixTimeSeconds(await chain.EpochStartAsync(current, ct));
        for (var epoch = current > Lookback ? current - Lookback : 0; epoch < current; epoch++)
        {
            if (store.GetMeasurement(_distributor, epoch) is not null)
            {
                continue;
            }

            var startMs = await chain.EpochStartAsync(epoch, ct) * 1000;
            var endMs = await chain.EpochStartAsync(epoch + 1, ct) * 1000;
            if (_time.GetUtcNow() < DateTimeOffset.FromUnixTimeMilliseconds(endMs) + MeasureGrace)
            {
                var start = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
                hold = start < hold ? start : hold;
                continue;
            }

            var factors = store.Aggregate(startMs, endMs);
            store.SaveMeasurement(new EpochMeasurement(_distributor, epoch, startMs, endMs, factors, _time.GetUtcNow().ToUnixTimeMilliseconds()));
            logger.LogInformation("Epoch {Epoch} measured: uptime {Uptime} bps, success {Success} bps, p95 {P95} ms, {Bytes} bytes over {Requests} requests.",
                epoch, factors.UptimeBps, factors.SuccessBps, factors.P95Ms, factors.BytesServed, factors.Requests);
        }

        recorder.HoldFrom = hold;
    }
}
