using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Repair;

/// <summary>
/// Periodic replication compliance scanner.
///
/// Every <see cref="GatewayOptions.ReplicationCheckIntervalSeconds"/> it pages through the full
/// metadata set and for each object:
///
///  a. Removes node_ids that are no longer in the live registry (departed nodes).
///  b. Issues an IntegrityChallenge to each replica to verify the blob is intact.
///  c. Removes replicas that fail the challenge.
///  d. Counts healthy replicas; if below MinCopiesPerObject, enqueues an UnderReplicated repair.
///
/// This is the network-wide enforcement of the redundancy rules defined in
/// Requirements/BlobStorage/redundancy-and-data-integrity.md.
/// At full network scale, region and fault-domain placement rules also apply here;
/// those require gossip-layer data and will be enforced once the P2P layer is implemented.
/// </summary>
public sealed class ReplicationMonitor : BackgroundService
{
    private const int PageSize = 200;

    private readonly GatewayMetadataStore _store;
    private readonly INodeRegistry _nodes;
    private readonly RepairQueue _repairQueue;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<ReplicationMonitor> _logger;

    public ReplicationMonitor(
        GatewayMetadataStore store,
        INodeRegistry nodes,
        RepairQueue repairQueue,
        IOptions<GatewayOptions> options,
        ILogger<ReplicationMonitor> logger)
    {
        _store = store;
        _nodes = nodes;
        _repairQueue = repairQueue;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay initial scan to let nodes come online after startup.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReplicationMonitor scan encountered an unexpected error.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_options.Value.ReplicationCheckIntervalSeconds), stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        var minCopies = _options.Value.MinCopiesPerObject;
        var knownUrls = _nodes.All.Select(n => n.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("ReplicationMonitor: starting compliance scan.");
        int scanned = 0, repairQueued = 0;

        var cursor = "";
        while (true)
        {
            var page = _store.ListAllObjectsPaged(cursor, PageSize);
            if (page.Count == 0) break;

            foreach (var record in page)
            {
                ct.ThrowIfCancellationRequested();

                // a. Prune node_ids for nodes that have left the registry.
                var knownReplicas = record.NodeIds
                    .Where(url => knownUrls.Contains(url))
                    .ToList();

                if (knownReplicas.Count != record.NodeIds.Count)
                {
                    _logger.LogDebug(
                        "Pruned {Count} stale node_ids from {Bucket}/{Key}.",
                        record.NodeIds.Count - knownReplicas.Count, record.Bucket, record.Key);
                    _store.UpdateNodeIds(record.Bucket, record.Key, knownReplicas);
                }

                // b+c. IntegrityChallenge each replica; remove any that fail.
                var healthyReplicas = new List<string>(knownReplicas.Count);
                foreach (var nodeUrl in knownReplicas)
                {
                    var node = _nodes.All.FirstOrDefault(n =>
                        n.Url.Equals(nodeUrl, StringComparison.OrdinalIgnoreCase));

                    if (node is null || !node.IsHealthy)
                    {
                        // Offline but within grace period — keep it, don't challenge.
                        healthyReplicas.Add(nodeUrl);
                        continue;
                    }

                    var intact = await ChallengeAsync(node, record.Cid, ct);
                    if (intact)
                    {
                        healthyReplicas.Add(nodeUrl);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Integrity challenge failed for CID {Cid} on node {Url}; removing replica.",
                            record.Cid, nodeUrl);
                        _repairQueue.EnqueueCorruptReplica(record, nodeUrl, _store, _nodes);
                        repairQueued++;
                    }
                }

                // d. Under-replicated? Enqueue a repair to add more copies.
                if (healthyReplicas.Count < minCopies)
                {
                    _logger.LogWarning(
                        "Object {Bucket}/{Key} has {Have} healthy replica(s), need {Need}; queuing repair.",
                        record.Bucket, record.Key, healthyReplicas.Count, minCopies);
                    _repairQueue.Enqueue(new RepairTask(record.Cid, null, RepairReason.UnderReplicated));
                    repairQueued++;
                }

                scanned++;
            }

            if (page.Count < PageSize) break;
            cursor = $"{page[^1].Bucket}/{page[^1].Key}";

            // Yield between pages to avoid saturating I/O during live traffic.
            await Task.Yield();
        }

        _logger.LogInformation(
            "ReplicationMonitor: scan complete. Scanned {Scanned} objects, queued {Repair} repair tasks.",
            scanned, repairQueued);
    }

    private async Task<bool> ChallengeAsync(NodeConnection node, string cid, CancellationToken ct)
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
            // Network error during challenge — treat as unknown, not as failure.
            // The grace-period logic in NodeRegistry will catch persistent outages.
            return true;
        }
    }
}
