using System.Threading.Channels;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Repair;

public enum RepairReason { CorruptReplica, MissingReplica, UnderReplicated }

public sealed record RepairTask(
    string Cid,
    string? BadNodeId,   // null = no specific bad node, just add a copy
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

    // CIDs queued and not yet processed: sweeps run on every node event, so without this the
    // same objects would be queued over and over (issue #135 review).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Queues the task unless the same CID is already waiting; returns whether it was queued.</summary>
    public bool Enqueue(RepairTask task)
    {
        if (!_pending.TryAdd(task.Cid, 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(task))
        {
            return true;
        }

        _pending.TryRemove(task.Cid, out _);
        return false;
    }

    /// <summary>Called by the worker once a task is finished (successfully or not).</summary>
    public void Done(string cid) => _pending.TryRemove(cid, out _);

    /// <summary>How many repairs are waiting.</summary>
    public int Pending => _pending.Count;

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
        // Re-reads inside a transaction and checks the CID, so a stale record (the key was
        // overwritten meanwhile) can't rewrite the newer version's replica list.
        store.RemoveReplica(record.Bucket, record.Key, record.Cid, badNodeUrl);
    }
}
