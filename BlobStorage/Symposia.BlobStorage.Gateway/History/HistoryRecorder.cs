using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Symposia.BlobStorage.Gateway.History;

/// <summary>
/// Records operations off the request path (issue #204 FR5): a bounded queue that drops new entries
/// when full (counted, and logged at most once a minute), drained by one writer in batched
/// transactions, so a slow history store never slows or fails an S3 call. History is best-effort,
/// like S3 server access logs. The same service sweeps entries past retention in bounded batches.
/// </summary>
public sealed class HistoryRecorder : BackgroundService
{
    public const string MeterName = "Symposia.BlobStorage.Gateway.History";
    private const int WriteBatch = 500;
    private const int SweepBatch = 10_000;

    private readonly Channel<HistoryEntry> _queue;
    private readonly HistoryStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<HistoryRecorder> _logger;
    private readonly Counter<long> _recorded;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _expired;
    private readonly TimeSpan _sweepInterval;
    private long _lastDropLogMinute = -1;

    public HistoryRecorder(HistoryStore store, IOptions<GatewayOptions> options, ILogger<HistoryRecorder> logger, IMeterFactory? meters = null,
        TimeProvider? time = null)
    {
        var o = options.Value;
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Retention = RetentionFor(o);
        _sweepInterval = TimeSpan.FromSeconds(Math.Max(1, o.History.SweepIntervalSeconds));
        _queue = Channel.CreateBounded<HistoryEntry>(new BoundedChannelOptions(Math.Max(1, o.History.QueueCapacity))
        {
            // Wait, not DropWrite: DropWrite discards silently and still reports success, so drops
            // couldn't be counted. Nothing awaits a write; TryWrite simply fails when full.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        var meter = meters?.Create(MeterName) ?? new Meter(MeterName);
        _recorded = meter.CreateCounter<long>("blob_history.recorded");
        _dropped = meter.CreateCounter<long>("blob_history.dropped");
        _expired = meter.CreateCounter<long>("blob_history.expired");
        meter.CreateObservableGauge("blob_history.queue_depth", () => _queue.Reader.Count);
    }

    /// <summary>How long entries are kept.</summary>
    public TimeSpan Retention { get; }

    /// <summary>
    /// 1 hour on the LocalDev chain (31337); 30 days anywhere else, including a gateway with no chain
    /// configured, the safe default for an audit log. <c>Gateway:History:RetentionHours</c> overrides,
    /// capped at 30 days.
    /// </summary>
    public static TimeSpan RetentionFor(GatewayOptions options)
    {
        const int maxHours = 720;
        if (options.History.RetentionHours is { } hours)
        {
            return TimeSpan.FromHours(Math.Clamp(hours, 1, maxHours));
        }

        return options.ChainId == 31337 ? TimeSpan.FromHours(1) : TimeSpan.FromHours(maxHours);
    }

    public void Record(HistoryEntry entry)
    {
        if (_queue.Writer.TryWrite(entry))
        {
            return;
        }

        _dropped.Add(1);
        var minute = Environment.TickCount64 / 60_000;
        if (Interlocked.Exchange(ref _lastDropLogMinute, minute) != minute)
        {
            _logger.LogWarning("Operation history queue is full; dropping entries (blob_history.dropped).");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweeper = SweepLoopAsync(stoppingToken);
        var batch = new List<HistoryEntry>(WriteBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < WriteBatch && _queue.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                Flush(batch);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Drain what's queued at shutdown.
        while (_queue.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
        }

        Flush(batch);
        await sweeper;
    }

    /// <summary>Writes whatever is queued now; for tests.</summary>
    internal void FlushNow()
    {
        var batch = new List<HistoryEntry>();
        while (_queue.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
        }

        Flush(batch);
    }

    /// <summary>One retention pass: deletes expired entries in bounded batches.</summary>
    internal int Sweep()
    {
        var cutoff = _time.GetUtcNow() - Retention;
        int deleted, total = 0;
        do
        {
            deleted = _store.DeleteOlderThan(cutoff, SweepBatch);
            total += deleted;
        }
        while (deleted == SweepBatch);

        if (total > 0)
        {
            _expired.Add(total);
            _store.IncrementalVacuum();
        }

        return total;
    }

    private void Flush(List<HistoryEntry> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            _store.Append(batch);
            foreach (var group in batch.GroupBy(e => e.Operation))
            {
                _recorded.Add(group.Count(), new KeyValuePair<string, object?>("operation", group.Key));
            }
        }
        catch (Exception ex)
        {
            _dropped.Add(batch.Count);
            _logger.LogWarning("Could not write {Count} operation history entries: {Reason}", batch.Count, ex.Message);
        }

        batch.Clear();
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_sweepInterval);
        try
        {
            do
            {
                try
                {
                    Sweep();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Operation history sweep failed: {Reason}", ex.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
