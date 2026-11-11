using Symposia.BlobStorage.StorageNode.Storage;
using Symposia.Network.Node.Telemetry;

namespace Symposia.BlobStorage.StorageNode;

/// <summary>
/// Storage-role heartbeat readings (issue #75): used bytes and available capacity, the
/// "available/used storage" inputs of tokenomics-mvp.md §6.3. Available is min(unused allocation,
/// free disk − reserve) (#199), so a node can't claim capacity its disk doesn't have. Read from the capacity monitor's snapshot (#197), so a
/// heartbeat no longer walks every blob on disk. Throughput/latency are left to verifier attestations.
/// </summary>
public sealed class StorageHeartbeatMetricsSource(StorageCapacityMonitor capacity) : IHeartbeatMetricsSource
{
    public ValueTask<HeartbeatReadings> ReadAsync(CancellationToken ct)
    {
        var s = capacity.Current;
        return ValueTask.FromResult(new HeartbeatReadings(AvailableBytes: (ulong)s.Available, UsedBytes: (ulong)Math.Max(0, s.Used)));
    }
}
