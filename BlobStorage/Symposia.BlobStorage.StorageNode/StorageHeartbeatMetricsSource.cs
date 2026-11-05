using Microsoft.Extensions.Options;
using Symposia.BlobStorage.StorageNode.Storage;
using Symposia.Network.Node.Telemetry;

namespace Symposia.BlobStorage.StorageNode;

/// <summary>
/// Storage-role heartbeat readings (issue #75): used bytes on disk and available capacity,
/// the "available/used storage" inputs of tokenomics-mvp.md §6.3. Available is capped by both
/// the operator's configured capacity and real free space on the volume, so a node can't claim
/// capacity its disk doesn't have. Throughput/latency are left to verifier attestations.
/// </summary>
public sealed class StorageHeartbeatMetricsSource(LocalBlobStore store, IOptions<StorageNodeOptions> options) : IHeartbeatMetricsSource
{
    public ValueTask<HeartbeatReadings> ReadAsync(CancellationToken ct)
    {
        ulong used = 0;
        foreach (var cid in store.EnumerateCids())
        {
            ct.ThrowIfCancellationRequested();
            used += (ulong)store.GetLength(cid);
        }

        var configured = (ulong)Math.Max(0, options.Value.MaxCapacityBytes);
        var headroom = configured > used ? configured - used : 0;
        var root = Path.GetFullPath(options.Value.StorageRoot);
        var free = Directory.Exists(root) ? (ulong)new DriveInfo(root).AvailableFreeSpace : 0;
        return ValueTask.FromResult(new HeartbeatReadings(AvailableBytes: Math.Min(headroom, free), UsedBytes: used));
    }
}
