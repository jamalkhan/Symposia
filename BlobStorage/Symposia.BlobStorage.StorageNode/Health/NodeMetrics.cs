using System.Diagnostics;
using Prometheus;

namespace Symposia.BlobStorage.StorageNode.Health;

/// <summary>
/// The storage node's Prometheus metric families (#1 FR-26), named as in
/// Requirements/BlobStorage/health-check-endpoints.md. I/O is counted by the gRPC service as it streams;
/// throughput and IOPS gauges are rates over the interval since the previous scrape. Families whose
/// source doesn't exist on this node yet (SMART attributes, the epoch uptime and heartbeat windows,
/// peer latency) are still exposed so dashboards bind to them, reading NaN until a source lands.
/// </summary>
public sealed class NodeMetrics
{
    private long _readBytes, _writeBytes, _readOps, _writeOps, _inFlight;
    private long _lastReadBytes, _lastWriteBytes, _lastReadOps, _lastWriteOps;
    private long _lastScrape = Stopwatch.GetTimestamp();
    private readonly Lock _rateLock = new();

    public NodeMetrics()
    {
        Registry = Metrics.NewCustomRegistry();
        var f = Metrics.WithCustomRegistry(Registry);

        StorageAvailable = f.CreateGauge("node_storage_available_bytes", "Allocated bytes still free for blobs.");
        StorageUsed = f.CreateGauge("node_storage_used_bytes", "Bytes stored.");
        BlobCount = f.CreateGauge("node_blob_count", "Blobs in the manifest.");

        ReadThroughput = f.CreateGauge("node_read_throughput_bytes_per_second", "Blob bytes read per second since the last scrape.");
        WriteThroughput = f.CreateGauge("node_write_throughput_bytes_per_second", "Blob bytes written per second since the last scrape.");
        IopsRead = f.CreateGauge("node_iops_read", "Read calls per second since the last scrape.");
        IopsWrite = f.CreateGauge("node_iops_write", "Write calls per second since the last scrape.");
        IoQueueDepth = f.CreateGauge("node_io_queue_depth", "Storage calls in flight.");

        NetworkInbound = f.CreateCounter("node_network_inbound_bytes_total", "Blob bytes received from gateways and peers.");
        NetworkOutbound = f.CreateCounter("node_network_outbound_bytes_total", "Blob bytes sent to gateways and peers.");
        PeerLatency = f.CreateSummary("node_peer_latency_seconds", "Handshake latency to peers.",
            new SummaryConfiguration { LabelNames = ["peer_id"], Objectives = [new(0.5, 0.05), new(0.99, 0.001)] });

        ReplicationTasksActive = f.CreateGauge("node_replication_tasks_active", "Repair copies currently being received.");
        ReplicationBytes = f.CreateCounter("node_replication_bytes_total", "Bytes received as repair copies.");
        IntegrityFailures = f.CreateCounter("node_integrity_check_failures_total", "Blobs found corrupt or missing by self-check or challenge.");

        PenaltyStage = f.CreateGauge("node_penalty_stage", "0=none, 1=warning, 2=degraded, 3=slash, 4=hard-slash.");
        UptimePct = f.CreateGauge("node_uptime_pct", "Uptime over the rolling epoch window.");
        HeartbeatCompliancePct = f.CreateGauge("node_heartbeat_compliance_pct", "Heartbeats delivered over the rolling epoch window.");

        SmartReallocated = f.CreateGauge("node_smart_reallocated_sectors", "SMART reallocated sector count.");
        SmartPending = f.CreateGauge("node_smart_pending_sectors", "SMART pending sector count.");
        SmartUncorrectable = f.CreateGauge("node_smart_uncorrectable_errors", "SMART uncorrectable error count.");
        SmartTemperature = f.CreateGauge("node_smart_temperature_celsius", "SMART drive temperature.");
        SmartWear = f.CreateGauge("node_smart_wear_level_pct", "SMART SSD wear level.");
        foreach (var unmeasured in new[] { UptimePct, HeartbeatCompliancePct, SmartReallocated, SmartPending, SmartUncorrectable, SmartTemperature, SmartWear })
        {
            unmeasured.Set(double.NaN);
        }
    }

    public CollectorRegistry Registry { get; }

    public Gauge StorageAvailable { get; }
    public Gauge StorageUsed { get; }
    public Gauge BlobCount { get; }
    public Gauge ReadThroughput { get; }
    public Gauge WriteThroughput { get; }
    public Gauge IopsRead { get; }
    public Gauge IopsWrite { get; }
    public Gauge IoQueueDepth { get; }
    public Counter NetworkInbound { get; }
    public Counter NetworkOutbound { get; }
    public Summary PeerLatency { get; }
    public Gauge ReplicationTasksActive { get; }
    public Counter ReplicationBytes { get; }
    public Counter IntegrityFailures { get; }
    public Gauge PenaltyStage { get; }
    public Gauge UptimePct { get; }
    public Gauge HeartbeatCompliancePct { get; }
    public Gauge SmartReallocated { get; }
    public Gauge SmartPending { get; }
    public Gauge SmartUncorrectable { get; }
    public Gauge SmartTemperature { get; }
    public Gauge SmartWear { get; }

    /// <summary>Starts a storage call; dispose when it ends (queue depth).</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _inFlight);
        return new End(this);
    }

    public void Read(long bytes)
    {
        Interlocked.Add(ref _readBytes, bytes);
        NetworkOutbound.Inc(bytes);
    }

    public void Wrote(long bytes)
    {
        Interlocked.Add(ref _writeBytes, bytes);
        NetworkInbound.Inc(bytes);
    }

    public void ReadOp() => Interlocked.Increment(ref _readOps);

    public void WriteOp() => Interlocked.Increment(ref _writeOps);

    /// <summary>Turns the counts since the last scrape into the rate gauges.</summary>
    public void UpdateRates()
    {
        lock (_rateLock)
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = Math.Max(Stopwatch.GetElapsedTime(_lastScrape, now).TotalSeconds, 0.001);
            _lastScrape = now;
            ReadThroughput.Set(Delta(ref _readBytes, ref _lastReadBytes) / seconds);
            WriteThroughput.Set(Delta(ref _writeBytes, ref _lastWriteBytes) / seconds);
            IopsRead.Set(Delta(ref _readOps, ref _lastReadOps) / seconds);
            IopsWrite.Set(Delta(ref _writeOps, ref _lastWriteOps) / seconds);
            IoQueueDepth.Set(Interlocked.Read(ref _inFlight));
        }
    }

    private static long Delta(ref long total, ref long last)
    {
        var current = Interlocked.Read(ref total);
        var delta = current - last;
        last = current;
        return delta;
    }

    private sealed class End(NodeMetrics metrics) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref metrics._inFlight);
            }
        }
    }
}
