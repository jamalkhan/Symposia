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

    public RepairWorker(
        RepairQueue queue,
        GatewayMetadataStore store,
        INodeRegistry nodes,
        ILogger<RepairWorker> logger)
    {
        _queue = queue;
        _store = store;
        _nodes = nodes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RepairAsync(task, stoppingToken);
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
        }
    }

    private async Task RepairAsync(RepairTask task, CancellationToken ct)
    {
        // 1. Get a representative metadata record (any object with this CID).
        var record = _store.GetObjectByCid(task.Cid);
        if (record is null)
        {
            _logger.LogDebug("Repair: CID {Cid} no longer in metadata; skipping.", task.Cid);
            return;
        }

        var currentNodeIds = record.NodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 2. Find a healthy source node that actually holds a good copy.
        var sourceNode = await FindHealthySourceAsync(task.Cid, currentNodeIds, task.BadNodeUrl, ct);
        if (sourceNode is null)
        {
            _logger.LogWarning(
                "Repair: no healthy source found for CID {Cid}. Will retry on next scan.", task.Cid);
            return;
        }

        // 3. Find a healthy target node not already in the replica set.
        var targetNode = _nodes.Healthy
            .Where(n => !currentNodeIds.Contains(n.Url, StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (targetNode is null)
        {
            _logger.LogDebug(
                "Repair: no available target node for CID {Cid} (all healthy nodes already have it).", task.Cid);
            return;
        }

        // 4. Replicate: read from source, write to target.
        _logger.LogInformation(
            "Repair: replicating CID {Cid} from {Source} to {Target} (reason: {Reason}).",
            task.Cid, sourceNode.Url, targetNode.Url, task.Reason);

        var success = await ReplicateAsync(record, sourceNode, targetNode, ct);
        if (!success) return;

        // 5. Update all objects that reference this CID to include the new node.
        UpdateAllReferencingObjects(task.Cid, targetNode.Url);

        // 6. If we evicted a bad node, schedule its blob for GC deletion.
        if (task.BadNodeUrl is not null && !_store.IsCidReferenced(task.Cid))
            _store.EnqueueNodeDeletion(task.Cid, task.BadNodeUrl);

        _logger.LogInformation("Repair complete for CID {Cid}: replica added on {Target}.", task.Cid, targetNode.Url);
    }

    private async Task<NodeConnection?> FindHealthySourceAsync(
        string cid, ISet<string> candidateUrls, string? excludeUrl, CancellationToken ct)
    {
        var candidates = _nodes.Healthy
            .Where(n => candidateUrls.Contains(n.Url, StringComparer.OrdinalIgnoreCase) &&
                        !n.Url.Equals(excludeUrl, StringComparison.OrdinalIgnoreCase))
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

    private void UpdateAllReferencingObjects(string cid, string newNodeUrl)
    {
        // Page through all objects to find every one referencing this CID and add the new node.
        var cursor = "";
        while (true)
        {
            var page = _store.ListAllObjectsPaged(cursor, 200);
            foreach (var obj in page)
            {
                if (!obj.Cid.Equals(cid, StringComparison.OrdinalIgnoreCase)) continue;
                var updated = obj.NodeIds
                    .Append(newNodeUrl)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _store.UpdateNodeIds(obj.Bucket, obj.Key, updated);
            }
            if (page.Count < 200) break;
            cursor = $"{page[^1].Bucket}/{page[^1].Key}";
        }
    }
}
