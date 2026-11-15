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
///  d. Counts healthy replicas; if below the object's own copy target, enqueues an UnderReplicated repair.
///
/// This is the network-wide enforcement of the redundancy rules defined in
/// Requirements/BlobStorage/redundancy-and-data-integrity.md.
/// At full network scale, region and fault-domain placement rules also apply here;
/// those require gossip-layer data and will be enforced once the P2P layer is implemented.
/// </summary>
public sealed class ReplicationMonitor : BackgroundService
{
    private const int PageSize = 200;
    private const int UnderReplicatedBatch = 500;

    // Coalescing wake-up signal: a node coming online or a short write asks for a prompt
    // under-replication sweep instead of waiting for the next full scan (issue #135).
    private readonly System.Threading.Channels.Channel<bool> _sweepRequests =
        System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
        });

    private readonly GatewayMetadataStore _store;
    private readonly INodeRegistry _nodes;
    private readonly RepairQueue _repairQueue;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<ReplicationMonitor> _logger;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Integrity.ChallengeScheduler? _challenges;

    public ReplicationMonitor(
        GatewayMetadataStore store,
        INodeRegistry nodes,
        RepairQueue repairQueue,
        IOptions<GatewayOptions> options,
        ILogger<ReplicationMonitor> logger,
        Integrity.ChallengeScheduler? challenges = null)
    {
        _challenges = challenges;
        _store = store;
        _nodes = nodes;
        _repairQueue = repairQueue;
        _options = options;
        _logger = logger;
    }

    /// <summary>Asks for a prompt under-replication sweep; repeated requests coalesce.</summary>
    public void RequestSweep() => _sweepRequests.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short settle so the first probes land, then catch anything left short before a restart.
        // Kept brief: sweep requests (a node coming online) wait behind it.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        RequestSweep();

        var interval = TimeSpan.FromSeconds(_options.Value.ReplicationCheckIntervalSeconds);
        var nextFullScan = DateTimeOffset.UtcNow + interval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextFullScan)
                {
                    await ScanAsync(stoppingToken);
                    nextFullScan = DateTimeOffset.UtcNow + interval;
                }
                else
                {
                    SweepUnderReplicated();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReplicationMonitor scan encountered an unexpected error.");
            }

            // Sleep until the next full scan or an early sweep request, whichever is first.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(nextFullScan - DateTimeOffset.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero);
            try
            {
                await _sweepRequests.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Full-scan time reached.
            }
        }
    }

    /// <summary>
    /// Queues a repair for every object recorded on fewer nodes than its own copy target (#7: targets follow
    /// each object's region assignment). Index-backed (idx_objects_short), so it's cheap enough to run
    /// whenever a node comes online or a write lands short.
    /// </summary>
    private void SweepUnderReplicated()
    {
        if (_nodes.All.Count(n => n.IsHealthy) <= 1)
        {
            _logger.LogDebug("ReplicationMonitor: no second healthy node to copy to yet; skipping sweep.");
            return;
        }

        // Page through every repairable object in (missing, bucket, key) order; the queue drops CIDs
        // already waiting, so repeated sweeps don't pile up duplicates.
        int found = 0, queued = 0;
        (int Missing, string Bucket, string Key)? cursor = null;
        while (true)
        {
            var page = _store.ListShortOfTarget(UnderReplicatedBatch, cursor);
            foreach (var record in page)
            {
                found++;
                if (_repairQueue.Enqueue(new RepairTask(record.Cid, null, RepairReason.UnderReplicated)))
                {
                    queued++;
                }
            }

            if (page.Count < UnderReplicatedBatch)
            {
                break;
            }

            cursor = (page[^1].TargetCopies - page[^1].NodeIds.Count, page[^1].Bucket, page[^1].Key);
        }

        if (found == 0)
        {
            _logger.LogDebug("ReplicationMonitor: no under-replicated objects.");
            return;
        }

        _logger.LogInformation("ReplicationMonitor: {Found} under-replicated object(s), {Queued} newly queued for repair.", found, queued);
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        var knownUrls = _nodes.All.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pruning by absence is only safe when the node list is the operator's static config.
        // Registry-sourced nodes live in memory and appear only once a sync reaches them, so after a
        // restart (or with the chain gateway down) a node that holds copies may simply not be loaded
        // yet; pruning then would erase the only record of its replicas (issue #135 review).
        var prune = _options.Value.NodeSource == NodeSourceKind.Static;

        _logger.LogInformation("ReplicationMonitor: starting compliance scan.");
        int scanned = 0, repairQueued = 0;

        (string Bucket, string Key)? cursor = null;
        while (true)
        {
            var page = _store.ListAllObjectsPaged(cursor, PageSize);
            if (page.Count == 0) break;

            foreach (var record in page)
            {
                ct.ThrowIfCancellationRequested();

                // a. Prune node_ids for nodes that have left the (static) node list.
                var knownReplicas = prune
                    ? record.NodeIds.Where(url => knownUrls.Contains(url)).ToList()
                    : [.. record.NodeIds];

                if (knownReplicas.Count != record.NodeIds.Count)
                {
                    _logger.LogDebug(
                        "Pruned {Count} stale node_ids from {Bucket}/{Key}.",
                        record.NodeIds.Count - knownReplicas.Count, record.Bucket, record.Key);
                    _store.UpdateNodeIds(record.Bucket, record.Key, record.Cid, knownReplicas);
                }

                if (record.IsErasureCoded)
                {
                    repairQueued += ScanShards(record) ? 1 : 0;
                    scanned++;
                    continue;
                }

                // b+c. Challenge each online replica (a Merkle leaf proof where a root is recorded, #8 FR6.4);
                //      remove any that fail. Offline replicas within the grace period still count.
                var grace = TimeSpan.FromSeconds(_options.Value.NodeGracePeriodSeconds);
                var online = new List<NodeConnection>();
                var counted = new List<NodeConnection?>();
                foreach (var nodeUrl in knownReplicas)
                {
                    var node = _nodes.All.FirstOrDefault(n =>
                        n.Id.Equals(nodeUrl, StringComparison.OrdinalIgnoreCase));

                    if (node is null || !node.IsHealthy || node.PenaltyStage >= 3)
                    {
                        // Offline within the grace period: still counts (don't challenge). Past it, or on a
                        // node that left the node list (once this gateway has run long enough to have
                        // loaded every node), it no longer counts, so repair adds a copy elsewhere (#137).
                        // A stage 3+ node is out of service: its copies don't count (#8 FR3.1).
                        var counts = node is { PenaltyStage: >= 3 } ? false : node?.CountsAsReplica(grace) ?? DateTimeOffset.UtcNow - _startedAt < grace;
                        if (counts)
                        {
                            counted.Add(node);
                        }
                        else
                        {
                            // Past the grace period: the reference is Offline until re-replication confirms (#16 FR4.1).
                            _store.MarkOffline(record.Bucket, record.Key, record.Cid, nodeUrl);
                        }

                        continue;
                    }

                    var result = await Integrity.ChallengeScheduler.ChallengeAsync(node, record, TimeSpan.FromSeconds(30), ct);
                    if (result is Integrity.ChallengeResult.Passed or Integrity.ChallengeResult.Unreachable)
                    {
                        // Unreachable is not evidence of loss: the grace-period logic catches persistent outages.
                        online.Add(node);
                        counted.Add(node);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Integrity challenge failed for CID {Cid} on node {Url}; removing replica.",
                            record.Cid, nodeUrl);
                        _challenges?.Record(node, record, result);
                        if (_challenges is null)
                        {
                            _repairQueue.EnqueueCorruptReplica(record, nodeUrl, _store, _nodes);
                        }

                        repairQueued++;
                    }
                }

                // d. Classify against the object's own target into exactly one tier (#8 FR4.1).
                var target = Math.Max(1, record.TargetCopies);

                // Re-replication confirmed healthy: the Offline references can go (#16 FR4.3). Until then they
                // stay, so a node that comes back after all isn't forgotten.
                if (online.Count >= target)
                {
                    foreach (var offline in _store.OfflineReplicas(record.Bucket, record.Key))
                    {
                        _store.PurgeOfflineReplica(record.Bucket, record.Key, record.Cid, offline);
                    }
                }

                var observation = new ReplicaObservation(
                    target, online.Count, counted.Count,
                    counted.Count(n => n is { PenaltyStage: >= 2 }),
                    online.Count(n => n.PenaltyStage == 1),
                    SoftViolationFixable: SoftGapFixable(record, online),
                    OverflowFixable: OverflowFixable(record, online),
                    CanGrow: CanGrow(record, online));
                if (RepairClassifier.Classify(observation) is { } tier)
                {
                    if (tier.Reason == RepairReason.OpportunisticRedundancy)
                    {
                        // #8 FR8: one more copy in a region the object isn't in yet, up to the governance maximum.
                        _store.RaiseTargetCopies(record.Bucket, record.Key, record.Cid, record.NodeIds.Count + 1);
                    }

                    var bad = tier.Reason == RepairReason.PlacementImprovement ? ConflictingReplica(record, online)?.Id : null;
                    if (_repairQueue.Enqueue(new RepairTask(record.Cid, bad, tier.Reason) { Priority = tier.Priority }))
                    {
                        _logger.LogInformation("Object {Bucket}/{Key}: {Online} online / {Counted} counted of {Need}; queued {Reason} at {Priority}.",
                            record.Bucket, record.Key, online.Count, counted.Count, target, tier.Reason, tier.Priority);
                        repairQueued++;
                    }
                }

                scanned++;
            }

            if (page.Count < PageSize) break;
            cursor = (page[^1].Bucket, page[^1].Key);

            // Yield between pages to avoid saturating I/O during live traffic.
            await Task.Yield();
        }

        _logger.LogInformation(
            "ReplicationMonitor: scan complete. Scanned {Scanned} objects, queued {Repair} repair tasks.",
            scanned, repairQueued);
    }

    /// <summary>Shards of an erasure-coded object that still serve; P0 once no more than k remain (#8 FR5.3).</summary>
    private bool ScanShards(ObjectRecord record)
    {
        if (!Domain.Erasure.ErasureScheme.TryParse(record.Scheme, out var scheme))
        {
            return false;
        }

        var grace = TimeSpan.FromSeconds(_options.Value.NodeGracePeriodSeconds);
        var shards = _store.Shards(record.Bucket, record.Key);
        var nodesOf = shards.Select(s => _nodes.All.FirstOrDefault(n => n.Id.Equals(s.NodeId, StringComparison.OrdinalIgnoreCase))).ToList();
        var online = nodesOf.Count(n => n is { IsHealthy: true, PenaltyStage: < 3 });
        var counted = nodesOf.Count(n => n is { PenaltyStage: < 3 } && n.CountsAsReplica(grace));
        if (counted >= scheme.TotalShards)
        {
            return false;
        }

        var priority = online <= scheme.DataShards ? RepairPriority.P0 : RepairPriority.P1;
        return _repairQueue.Enqueue(new RepairTask(record.Cid, null, RepairReason.UnderReplicated) { Priority = priority });
    }

    /// <summary>A recorded soft gap (subnet/ASN/facility) whose conflicting copy now has a compliant alternative.</summary>
    private bool SoftGapFixable(ObjectRecord record, IReadOnlyList<NodeConnection> online) =>
        record.PlacementGaps.Any(g => g.Rule is "subnet" or "asn" or "facility") && ConflictingReplica(record, online) is not null;

    /// <summary>The copy that shares a soft fault domain with another, when a writable node shares none with the rest.</summary>
    private NodeConnection? ConflictingReplica(ObjectRecord record, IReadOnlyList<NodeConnection> online)
    {
        foreach (var copy in online)
        {
            var others = online.Where(o => o != copy).ToList();
            if (Placement.PlacementEngine.SoftConflicts(copy, others).Count == 0)
            {
                continue;
            }

            var better = _nodes.Writable(record.SizeBytes).Any(n => !online.Contains(n) && n.PenaltyStage < 3
                && (n.PublicIp.Length == 0 || others.All(o => o.PublicIp != n.PublicIp))
                && Placement.PlacementEngine.SoftConflicts(n, others).Count == 0
                && (record.Regions.Count == 0 || record.Regions.Contains(n.Region, StringComparer.OrdinalIgnoreCase) || !record.Regions.Contains(copy.Region, StringComparer.OrdinalIgnoreCase)));
            if (better)
            {
                return copy;
            }
        }

        return null;
    }

    /// <summary>An overflow copy shares a region (recorded at write) and a region with no copy now has writable nodes.</summary>
    private bool OverflowFixable(ObjectRecord record, IReadOnlyList<NodeConnection> online) =>
        record.PlacementGaps.Any(g => g.Rule == "overflow-region") && FreshRegionAvailable(record, online);

    /// <summary>P3 growth (#8 FR8): only global objects (a constrained one never leaves its regions, FR8.2), up to the maximum.</summary>
    private bool CanGrow(ObjectRecord record, IReadOnlyList<NodeConnection> online) =>
        _options.Value.Redundancy.Mode == RedundancyMode.Network && record.Regions.Count == 0
        && record.NodeIds.Count >= record.TargetCopies && record.NodeIds.Count < _options.Value.Redundancy.MaxReplicationFactor
        && FreshRegionAvailable(record, online);

    private bool FreshRegionAvailable(ObjectRecord record, IReadOnlyList<NodeConnection> online)
    {
        var held = online.Select(n => n.Region).Where(r => r.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = record.Regions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _nodes.Writable(record.SizeBytes).Any(n => n.Region.Length > 0 && !held.Contains(n.Region) && !targets.Contains(n.Region) && n.PenaltyStage < 3);
    }
}
