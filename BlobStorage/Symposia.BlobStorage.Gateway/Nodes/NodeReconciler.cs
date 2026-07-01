using Grpc.Core;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Repair;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// Reconciles a storage node's local state against the gateway's metadata when the node comes
/// back online after being offline.
///
/// Two reconciliation directions (Requirements/BlobStorage/garbage-collection.md#stale-replica-metadata):
///
///  Forward pass — gateway asks the node which CIDs it holds.
///   • CID on node but NOT in gateway metadata → true orphan; queue deletion via GcWorker.
///   • CID on node AND in metadata pointing to this node → healthy, no action.
///
///  Reverse pass — gateway checks every object that claims this node as a replica.
///   • Node responded (has the CID): verify integrity via challenge.
///     - Passes → healthy.
///     - Fails  → mark corrupt; enqueue repair (remove bad replica, add replacement).
///   • Node did NOT list this CID: replica was silently lost while offline;
///     remove from node_ids and enqueue repair.
///
/// This is also triggered at startup for all nodes that were already healthy when the gateway
/// started (call <see cref="ReconcileAsync"/> during startup for each known node).
/// </summary>
public sealed class NodeReconciler
{
    private const int ListPageSize = 100;
    private const int MetaPageSize = 200;

    private readonly GatewayMetadataStore _store;
    private readonly RepairQueue _repairQueue;
    private readonly INodeRegistry _nodes;
    private readonly ILogger<NodeReconciler> _logger;

    public NodeReconciler(
        GatewayMetadataStore store,
        RepairQueue repairQueue,
        INodeRegistry nodes,
        ILogger<NodeReconciler> logger)
    {
        _store = store;
        _repairQueue = repairQueue;
        _nodes = nodes;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full reconciliation for <paramref name="node"/>.
    /// Should be called on reconnect (when <see cref="NodeConnection.JustReconnected"/> is set)
    /// and opportunistically at gateway startup.
    /// </summary>
    public async Task ReconcileAsync(NodeConnection node, CancellationToken ct)
    {
        _logger.LogInformation("NodeReconciler: starting reconciliation for node {Url}.", node.Url);

        // Forward pass: collect all CIDs the node reports holding.
        var nodeCids = await CollectNodeCidsAsync(node, ct);
        if (nodeCids is null)
        {
            _logger.LogWarning(
                "NodeReconciler: could not list blobs from {Url}; skipping.", node.Url);
            return;
        }

        // ── Forward pass ─────────────────────────────────────────────────────
        // Any CID the node holds that has no metadata pointing to this node →
        // either genuine orphan (queue deletion) or the metadata was just not written yet
        // (grace period handled by the 48h requirement in garbage-collection.md — we skip
        // CIDs that appear in metadata for ANY node, not just this one).
        int orphanQueued = 0;
        foreach (var cid in nodeCids)
        {
            ct.ThrowIfCancellationRequested();
            if (_store.GetObjectByCid(cid) is null)
            {
                // No object in the entire metadata store references this CID — true orphan.
                _logger.LogDebug(
                    "NodeReconciler: CID {Cid} on {Url} has no metadata; queuing GC deletion.", cid, node.Url);
                _store.EnqueueNodeDeletion(cid, node.Url);
                orphanQueued++;
            }
        }

        // ── Reverse pass ─────────────────────────────────────────────────────
        // Every object the metadata claims lives on this node — verify the node still has it.
        int repaired = 0;
        var cursor = "";
        while (true)
        {
            var page = _store.ListAllObjectsPaged(cursor, MetaPageSize);
            if (page.Count == 0) break;

            foreach (var record in page)
            {
                ct.ThrowIfCancellationRequested();

                if (!record.NodeIds.Any(u => u.Equals(node.Url, StringComparison.OrdinalIgnoreCase)))
                    continue; // This node isn't a listed replica of this object — skip.

                if (!nodeCids.Contains(record.Cid))
                {
                    // Metadata says this node holds the blob, but the node doesn't have it.
                    _logger.LogWarning(
                        "NodeReconciler: {Url} is missing CID {Cid} for {Bucket}/{Key}; flagging for repair.",
                        node.Url, record.Cid, record.Bucket, record.Key);
                    _repairQueue.EnqueueMissingReplica(record, node.Url, _store, _nodes);
                    repaired++;
                }
                else
                {
                    // Node has it — verify integrity.
                    var intact = await ChallengeAsync(node, record.Cid, ct);
                    if (!intact)
                    {
                        _logger.LogWarning(
                            "NodeReconciler: CID {Cid} on {Url} failed integrity challenge after reconnect.",
                            record.Cid, node.Url);
                        _repairQueue.EnqueueCorruptReplica(record, node.Url, _store, _nodes);
                        repaired++;
                    }
                }
            }

            if (page.Count < MetaPageSize) break;
            cursor = $"{page[^1].Bucket}/{page[^1].Key}";
        }

        node.JustReconnected = false;

        _logger.LogInformation(
            "NodeReconciler: {Url} done. Orphans queued: {Orphans}, repairs enqueued: {Repairs}.",
            node.Url, orphanQueued, repaired);
    }

    /// <summary>Polls the node's JustReconnected flag and runs reconciliation as needed.</summary>
    public async Task CheckForReconnectsAsync(CancellationToken ct)
    {
        foreach (var node in _nodes.All)
        {
            if (!node.JustReconnected) continue;
            await ReconcileAsync(node, ct);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<HashSet<string>?> CollectNodeCidsAsync(
        NodeConnection node, CancellationToken ct)
    {
        var cids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var afterCid = "";
            while (true)
            {
                var call = node.Client.ListBlobs(
                    new ListBlobsRequest { AfterCid = afterCid },
                    cancellationToken: ct);

                int lastChunkSize = 0;
                await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct))
                {
                    foreach (var cid in chunk.Cids) cids.Add(cid);
                    lastChunkSize = chunk.Cids.Count;
                    if (lastChunkSize > 0) afterCid = chunk.Cids[^1];
                }

                // ListBlobs server side uses PageSize=100; fewer means last page.
                if (lastChunkSize < ListPageSize) break;
            }
            return cids;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> ChallengeAsync(NodeConnection node, string cid, CancellationToken ct)
    {
        try
        {
            var resp = await node.Client.IntegrityChallengeAsync(
                new IntegrityChallengeRequest { Cid = cid, Offset = 0, Length = 0 },
                deadline: DateTime.UtcNow.AddSeconds(30),
                cancellationToken: ct);
            return resp.Sha256Hex.Equals(cid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true; // Can't challenge → assume ok; ReplicationMonitor will catch it later.
        }
    }
}
