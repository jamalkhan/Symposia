using Google.Protobuf;
using Grpc.Core;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Repair;

/// <summary>
/// Drains the RepairQueue and re-replicates blobs from healthy source nodes to new target nodes.
///
/// Repair flow for each task:
///  1. Look up a representative ObjectRecord for this CID (for TenantId/Bucket/Key metadata).
///  2. Verify the bad node (if any) is removed from metadata (RepairQueue already does this).
///  3. Find a healthy source node that passes IntegrityChallenge for this CID.
///  4. Find a healthy target node that isn't already in the replica set.
///  5. Stream the blob from source → target using the existing gRPC WriteBlob protocol.
///  6. Update metadata to add the target node to the replica set.
///
/// See Requirements/BlobStorage/redundancy-and-data-integrity.md#offline-and-degradation-triggers.
/// </summary>
public sealed class RepairWorker : BackgroundService
{
    private const int ChunkSizeBytes = 64 * 1024;

    private readonly RepairQueue _queue;
    private readonly GatewayMetadataStore _store;
    private readonly INodeRegistry _nodes;
    private readonly ILogger<RepairWorker> _logger;
    private readonly Placement.PlacementEngine _placement;
    private readonly BandwidthBudget _budget;
    private readonly Erasure.ErasureObjects _erasure;

    public RepairWorker(
        RepairQueue queue,
        GatewayMetadataStore store,
        INodeRegistry nodes,
        Placement.PlacementEngine placement,
        BandwidthBudget budget,
        Erasure.ErasureObjects erasure,
        ILogger<RepairWorker> logger)
    {
        _placement = placement;
        _budget = budget;
        _erasure = erasure;
        _queue = queue;
        _store = store;
        _nodes = nodes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Work a previous run left open goes back in the queue (#8 FR4.5: the audit rows outlive memory).
        var rehydrated = _queue.Rehydrate();
        if (rehydrated > 0)
        {
            _logger.LogInformation("Re-queued {Count} repair task(s) left open by the previous run.", rehydrated);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            RepairTask task;
            try
            {
                task = await _queue.ReadAsync(stoppingToken); // most urgent tier first
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var outcome = "Failed";
            try
            {
                outcome = await RepairAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Repair failed for CID {Cid} (reason: {Reason}).", task.Cid, task.Reason);
            }
            finally
            {
                _queue.Done(task, outcome);
            }
        }
    }

    /// <summary>Repairs one task; returns its outcome for the audit row (Repaired, Skipped or Failed).</summary>
    private async Task<string> RepairAsync(RepairTask task, CancellationToken ct)
    {
        if (_store.GetObjectByCid(task.Cid) is { IsErasureCoded: true } coded)
        {
            return await RepairShardsAsync(coded, task, ct);
        }

        return await RepairReplicaAsync(task, ct);
    }

    /// <summary>Rebuilds every shard that's missing or on a node out of service (#8 FR5.3).</summary>
    private async Task<string> RepairShardsAsync(ObjectRecord record, RepairTask task, CancellationToken ct)
    {
        var shards = _store.Shards(record.Bucket, record.Key);
        var grace = TimeSpan.FromSeconds(900);
        var missing = Enumerable.Range(0, record.TargetCopies).Where(i =>
        {
            var shard = shards.FirstOrDefault(s => s.Index == i);
            var node = shard is null ? null : _nodes.All.FirstOrDefault(n => n.Id.Equals(shard.NodeId, StringComparison.OrdinalIgnoreCase));
            return node is null || node.PenaltyStage >= 3 || !node.CountsAsReplica(grace)
                || (task.BadNodeId is not null && node.Id.Equals(task.BadNodeId, StringComparison.OrdinalIgnoreCase));
        }).ToList();
        var rebuilt = 0;
        foreach (var index in missing)
        {
            if (await _erasure.RebuildShardAsync(record, index, _budget, ct) is not null)
            {
                rebuilt++;
            }
        }

        _logger.LogInformation("Repair of erasure-coded {Bucket}/{Key}: rebuilt {Rebuilt} of {Missing} shard(s).", record.Bucket, record.Key, rebuilt, missing.Count);
        return missing.Count == 0 ? "Skipped" : rebuilt == missing.Count ? "Repaired" : "Failed";
    }

    private async Task<string> RepairReplicaAsync(RepairTask task, CancellationToken ct)
    {
        // 1. Get a representative metadata record (any object with this CID).
        var record = _store.GetObjectByCid(task.Cid);
        if (record is null)
        {
            _logger.LogDebug("Repair: CID {Cid} no longer in metadata; skipping.", task.Cid);
            return "Skipped";
        }

        var currentNodeIds = record.NodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 2. Find a healthy source node that actually holds a good copy.
        var sourceNode = await FindHealthySourceAsync(task.Cid, currentNodeIds, task.BadNodeId, ct);
        if (sourceNode is null)
        {
            _logger.LogWarning(
                "Repair: no healthy source found for CID {Cid}. Will retry on next scan.", task.Cid);
            return "Failed";
        }

        // 3. Find a target under the object's placement rules (#7/#8): the region still short of its
        //    count first, then overflow; never a node already holding a copy.
        var existing = _nodes.All.Where(n => currentNodeIds.Contains(n.Id)).ToList();
        var requirement = _placement.Requirement(record.Regions, record.SizeBytes);
        var targetNode = _placement.PlaceAdditional(requirement, existing, 1, record.SizeBytes, exclude: currentNodeIds).Targets.FirstOrDefault();

        if (targetNode is null)
        {
            _logger.LogDebug(
                "Repair: no available target node for CID {Cid} (all healthy nodes already have it).", task.Cid);
            // Shown as a placement warning rather than silently dropped (#16 FR4.4).
            _store.FlagPlacementWarning(task.Cid, "no eligible node for another copy under its placement rules");
            return "Failed";
        }

        // 4. Replicate: read from source, write to target.
        _logger.LogInformation(
            "Repair: replicating CID {Cid} from {Source} to {Target} (reason: {Reason}).",
            task.Cid, sourceNode.Url, targetNode.Url, task.Reason);

        var success = await ReplicateAsync(record, sourceNode, targetNode, ct);
        if (!success) return "Failed";

        // 5. Record the new replica on every object holding this CID (idx_objects_cid, atomic).
        //    If the object was deleted or overwritten while copying, the target's copy is now
        //    orphaned storage: queue it for deletion instead of leaking it.
        _store.AddReplicaForCid(task.Cid, targetNode.Id);
        if (!_store.IsCidReferenced(task.Cid))
        {
            _logger.LogInformation("Repair: CID {Cid} was deleted while copying; removing the new copy from {Target}.", task.Cid, targetNode.Url);
            _store.EnqueueNodeDeletion(task.Cid, targetNode.Id);
            return "Skipped";
        }

        // 6. A penalized node being evacuated (#8 FR3 stage 3) loses its copy once the replacement landed.
        if (task.Reason is RepairReason.PenaltyPreReplication or RepairReason.PlacementImprovement && task.BadNodeId is not null)
        {
            foreach (var holder in _store.ObjectsOnNode(task.BadNodeId, 1000).Where(o => o.Cid == task.Cid))
            {
                _store.RemoveReplica(holder.Bucket, holder.Key, holder.Cid, task.BadNodeId);
            }

            // A slashed/terminated node's replicas are GC's highest priority (#16 FR8.2).
            _store.EnqueueNodeDeletion(task.Cid, task.BadNodeId,
                task.Reason == RepairReason.PenaltyPreReplication ? "SlashedNodePurge" : "StaleReplicaPurge",
                task.Reason == RepairReason.PenaltyPreReplication ? GatewayMetadataStore.PriorityHigh : GatewayMetadataStore.PriorityLow);
        }

        // 7. If we evicted a bad node, schedule its blob for GC deletion.
        if (task.BadNodeId is not null && !_store.IsCidReferenced(task.Cid))
            _store.EnqueueNodeDeletion(task.Cid, task.BadNodeId);

        if (task.Reason == RepairReason.PlacementImprovement && _store.GetObjectByCid(task.Cid) is { } improved)
        {
            // Re-derive the gaps from the copies now held (#8 FR2.4).
            var held = _nodes.All.Where(n => improved.NodeIds.Contains(n.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            var gaps = held.SelectMany(n => Placement.PlacementEngine.SoftConflicts(n, held.Where(o => o != n).ToList())
                .Select(rule => new PlacementGapRecord(rule, $"{n.Id} shares its {rule} with another copy"))).ToList();
            _store.SetPlacementGaps(improved.Bucket, improved.Key, improved.Cid, gaps);
        }

        if (_store.GetObjectByCid(task.Cid) is { } after && after.NodeIds.Count >= after.TargetCopies)
        {
            _store.CompletePendingReplicas(task.Cid); // #8 FR3.2: the extra copies asked for have landed
        }
        else if (_store.GetObjectByCid(task.Cid) is not null)
        {
            // Still short of target: keep going at the same tier (one copy per pass).
            _queue.Done(task, "Repaired");
            _queue.Enqueue(task with { AuditId = 0, TriggeredAt = task.TriggeredAt });
        }

        _store.ClearPlacementWarning(task.Cid);
        _logger.LogInformation("Repair complete for CID {Cid}: replica added on {Target}.", task.Cid, targetNode.Url);
        return "Repaired";
    }

    private async Task<NodeConnection?> FindHealthySourceAsync(
        string cid, ISet<string> candidateUrls, string? excludeUrl, CancellationToken ct)
    {
        var candidates = _nodes.Healthy
            .Where(n => candidateUrls.Contains(n.Id, StringComparer.OrdinalIgnoreCase) &&
                        !n.Id.Equals(excludeUrl, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var node in candidates)
        {
            try
            {
                var resp = await node.Client.IntegrityChallengeAsync(
                    new IntegrityChallengeRequest { Cid = cid, Offset = 0, Length = 0 },
                    deadline: DateTime.UtcNow.AddSeconds(30),
                    cancellationToken: ct);

                if (resp.Sha256Hex.Equals(cid, StringComparison.OrdinalIgnoreCase))
                    return node;

                _logger.LogWarning(
                    "IntegrityChallenge on {Url} for {Cid} returned wrong hash {Got}.",
                    node.Url, cid, resp.Sha256Hex);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "IntegrityChallenge on {Url} for {Cid} failed: {Msg}", node.Url, cid, ex.Message);
            }
        }
        return null;
    }

    private async Task<bool> ReplicateAsync(
        ObjectRecord record, NodeConnection source, NodeConnection target, CancellationToken ct)
    {
        try
        {
            using var writeCall = target.Client.WriteBlob(cancellationToken: ct);

            // Send header.
            await writeCall.RequestStream.WriteAsync(new WriteBlobChunk
            {
                Header = new WriteBlobHeader
                {
                    TenantId = "repair",  // internal repair operation, no tenant key context
                    Bucket = record.Bucket,
                    Key = record.Key,
                },
            }, ct);

            // Proxy blob bytes from source to target.
            var readCall = source.Client.ReadBlob(
                new ReadBlobRequest { Cid = record.Cid, Offset = 0, Length = 0 },
                cancellationToken: ct);

            await foreach (var chunk in readCall.ResponseStream.ReadAllAsync(ct))
            {
                // Both ends pay from the shared per-node budget (#8 FR3.3, AC11).
                await _budget.AcquireAsync([source, target], chunk.Data.Length, ct);
                await writeCall.RequestStream.WriteAsync(
                    new WriteBlobChunk { Data = chunk.Data }, ct);
            }

            await writeCall.RequestStream.CompleteAsync();
            var result = await writeCall.ResponseAsync;

            // Verify the target stored the correct blob.
            if (!result.Cid.Equals(record.Cid, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Replication CID mismatch: expected {Expected}, target wrote {Got}.",
                    record.Cid, result.Cid);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replication from {Source} to {Target} failed.", source.Url, target.Url);
            return false;
        }
    }
}
