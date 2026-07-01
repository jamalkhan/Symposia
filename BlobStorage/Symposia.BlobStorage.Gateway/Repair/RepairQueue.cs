using System.Threading.Channels;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Repair;

public enum RepairReason { CorruptReplica, MissingReplica, UnderReplicated }

public sealed record RepairTask(
    string Cid,
    string? BadNodeUrl,   // null = no specific bad node, just add a copy
    RepairReason Reason);

/// <summary>
/// In-memory channel of repair tasks. Tasks lost on restart are re-discovered on the next
/// ReplicationMonitor scan, so persistence is not needed.
/// </summary>
public sealed class RepairQueue
{
    private readonly Channel<RepairTask> _channel =
        Channel.CreateBounded<RepairTask>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<RepairTask> Reader => _channel.Reader;

    public void Enqueue(RepairTask task) => _channel.Writer.TryWrite(task);

    /// <summary>
    /// Called from GetObject when a CID mismatch is detected during streaming.
    /// Removes the bad node from the object's replica list and enqueues re-replication.
    /// </summary>
    public void EnqueueCorruptReplica(
        ObjectRecord record, string badNodeUrl,
        GatewayMetadataStore store, INodeRegistry nodes)
    {
        RemoveBadNodeFromMetadata(record, badNodeUrl, store);
        Enqueue(new RepairTask(record.Cid, badNodeUrl, RepairReason.CorruptReplica));
    }

    /// <summary>
    /// Called from GetObject when a node returns NotFound for a CID it should have.
    /// Removes the node from the replica list and enqueues re-replication.
    /// </summary>
    public void EnqueueMissingReplica(
        ObjectRecord record, string missingNodeUrl,
        GatewayMetadataStore store, INodeRegistry nodes)
    {
        RemoveBadNodeFromMetadata(record, missingNodeUrl, store);
        Enqueue(new RepairTask(record.Cid, missingNodeUrl, RepairReason.MissingReplica));
    }

    private static void RemoveBadNodeFromMetadata(
        ObjectRecord record, string badNodeUrl, GatewayMetadataStore store)
    {
        var updated = record.NodeIds
            .Where(u => !u.Equals(badNodeUrl, StringComparison.OrdinalIgnoreCase))
            .ToList();
        store.UpdateNodeIds(record.Bucket, record.Key, updated);
    }
}
