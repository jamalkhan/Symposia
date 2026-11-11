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
    private long _lastWriteFailureLogMinute = -1;
    private int _failures;

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
        // On-disk size (file + WAL), for disk accounting (#197/#199).
        meter.CreateObservableGauge("blob_history.db_bytes", () => store.SizeBytes);
    }

    /// <summary>How long entries are kept.</summary>
    public TimeSpan Retention { get; }

    /// <summary>The clock entries are stamped with.</summary>
    public DateTimeOffset Now => _time.GetUtcNow();

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

                if (!Flush(batch))
                {
                    // A failing store (disk full, locked) isn't hammered: back off up to 30 s. Entries queued
                    // meanwhile wait in the bounded queue, and overflow is counted as drops.
                    _failures = Math.Min(_failures + 1, 5);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << _failures)), stoppingToken);
                }
                else
                {
                    _failures = 0;
                }
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
            if (deleted == SweepBatch)
            {
                Thread.Yield(); // let the writer's next batch in between deletes
            }
        }
        while (deleted == SweepBatch);

        if (total > 0)
        {
            _expired.Add(total);
            _store.IncrementalVacuum();
        }

        _store.Checkpoint(); // keeps the WAL from growing without bound under steady writes

        return total;
    }

    private bool Flush(List<HistoryEntry> batch)
    {
        if (batch.Count == 0)
        {
            return true;
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
            var minute = Environment.TickCount64 / 60_000;
            if (Interlocked.Exchange(ref _lastWriteFailureLogMinute, minute) != minute)
            {
                _logger.LogWarning("Could not write {Count} operation history entries: {Reason}", batch.Count, ex.Message);
            }

            batch.Clear();
            return false;
        }

        batch.Clear();
        return true;
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        // Off the startup path: a long backlog of expired rows mustn't delay the gateway opening its port.
        await Task.Yield();
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
