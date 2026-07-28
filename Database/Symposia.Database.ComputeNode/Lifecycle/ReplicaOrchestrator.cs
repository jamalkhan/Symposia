using Symposia.Database.ComputeNode.Proxy;

namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// Read replica provisioning, resize, suspend/resume, deletion, lag tracking, and (minimal-scope,
/// manual-trigger) promotion, per the #96 architectural plan. Deliberately reuses #95's
/// <see cref="DatabaseLifecycleOrchestrator"/> only to validate/read the parent database's state
/// (region, existence) -- a replica is provisioned against the *existing* Tier 1 bucket with zero
/// page-data copy, so it does not go through #95's bucket-provisioning path at all, and its own
/// lifecycle is tracked independently so resize/suspend/delete on one replica never touches the
/// primary or any sibling replica.
/// </summary>
public sealed class ReplicaOrchestrator(
    DatabaseLifecycleOrchestrator primaryOrchestrator,
    IComputeNodePlacementService placement,
    IProxyRoutingClient routing)
{
    /// <summary>
    /// A few seconds' worth of WAL at a modest write rate, standing in for the tenant/governance-
    /// configurable lag threshold the Arch plan calls for (no concrete default was set at Arch
    /// time); a replica beyond this is marked <see cref="ReplicaStatus.Lagging"/> and excluded from
    /// the proxy's read-only rotation until it catches back up.
    /// </summary>
    public const long DefaultLagThresholdBytes = 8 * 1024 * 1024;

    private const int DefaultMaxConnections = 200;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DatabaseReplica> _replicas = [];
    private readonly Dictionary<string, SemaphoreSlim> _leases = [];

    public DatabaseReplica? GetState(string replicaId)
    {
        lock (_gate)
        {
            return _replicas.GetValueOrDefault(replicaId);
        }
    }

    public IReadOnlyList<DatabaseReplica> ListReplicas(string databaseId)
    {
        lock (_gate)
        {
            return _replicas.Values.Where(r => r.DatabaseId == databaseId).ToList();
        }
    }

    private SemaphoreSlim LeaseFor(string replicaId)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(replicaId, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _leases[replicaId] = semaphore;
            }
            return semaphore;
        }
    }

    private void Store(DatabaseReplica replica)
    {
        lock (_gate)
        {
            _replicas[replica.ReplicaId] = replica;
        }
    }

    /// <summary>
    /// Provisioning (FR1): selects a qualifying node in the parent database's region (same-region
    /// only, per Out of Scope) and registers a routing-table replica entry pointed at the primary's
    /// existing bucket -- no page copy, no new bucket, no safekeeper reassignment.
    /// </summary>
    public async Task<ReplicaOperationResult> ProvisionReplicaAsync(ProvisionReplicaRequest request, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(request.ReplicaId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            if (_replicas.ContainsKey(request.ReplicaId))
                return ReplicaOperationResult.Rejected($"Replica '{request.ReplicaId}' already exists.");

            var primary = primaryOrchestrator.GetState(request.DatabaseId);
            if (primary is null)
                return ReplicaOperationResult.NotFound(request.DatabaseId);

            if (primary.State is LifecycleState.Deleting or LifecycleState.Deleted)
                return ReplicaOperationResult.Rejected($"Cannot provision a replica for database '{request.DatabaseId}' in state '{primary.State}'.");

            var region = request.Region ?? primary.Region;
            if (region != primary.Region)
                return ReplicaOperationResult.Rejected("Cross-region replicas are out of scope; the replica region must match the primary's region.");

            var requiredTier = request.ComputeSize.RequiredTier();
            var existingReplicaNodeIds = routing.GetReplicas(request.DatabaseId).Select(r => r.NodeId);
            var exclude = existingReplicaNodeIds.Append(primary.PrimaryNodeId!).ToArray();
            var node = placement.SelectNode(region, requiredTier, exclude);
            if (node is null)
                return ReplicaOperationResult.NoQualifyingNode();

            routing.AddOrUpdateReplica(request.DatabaseId, new ComputeEndpoint(node.NodeId, node.Host, node.Port));

            var connectionString = $"postgres://{request.DatabaseId}-ro.{region}.db.symposia.network:5432/{request.DatabaseName}";
            var replica = new DatabaseReplica(
                request.ReplicaId, request.DatabaseId, region, request.ComputeSize,
                ReplicaStatus.Healthy, node.NodeId, ReplicaLsn: 0, LagBytes: 0, connectionString);
            Store(replica);

            return ReplicaOperationResult.Ok(replica);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>Resize independent of the primary and of sibling replicas (FR7): reassigns only if the new size needs a higher tier.</summary>
    public async Task<ReplicaOperationResult> ResizeReplicaAsync(string replicaId, DatabaseSize newSize, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(replicaId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(replicaId);
            if (current is null)
                return ReplicaOperationResult.NotFound(replicaId);

            if (current.Status is not (ReplicaStatus.Healthy or ReplicaStatus.Lagging))
                return ReplicaOperationResult.Rejected($"Cannot resize replica in status '{current.Status}'.");

            var requiredTier = newSize.RequiredTier();
            var stillQualifies = NodeStillQualifies(current.NodeId!, current.Region, requiredTier);

            var nodeId = current.NodeId!;
            if (!stillQualifies)
            {
                var node = placement.SelectNode(current.Region, requiredTier, excludeNodeIds: [current.NodeId!]);
                if (node is null)
                    return ReplicaOperationResult.NoQualifyingNode();

                routing.RemoveReplica(current.DatabaseId, current.NodeId!);
                routing.AddOrUpdateReplica(current.DatabaseId, new ComputeEndpoint(node.NodeId, node.Host, node.Port));
                nodeId = node.NodeId;
            }

            var resized = current with { ComputeSize = newSize, NodeId = nodeId };
            Store(resized);
            return ReplicaOperationResult.Ok(resized);
        }
        finally
        {
            lease.Release();
        }
    }

    private bool NodeStillQualifies(string nodeId, string region, int requiredTier)
    {
        var node = placement.GetNode(nodeId);
        return node is not null && node.Region == region && node.Tier <= requiredTier && node.AvailableCapacity > 0;
    }

    /// <summary>Suspend independent of the primary (FR7): drops the replica out of proxy rotation without affecting the primary's read-write availability.</summary>
    public async Task<ReplicaOperationResult> SuspendReplicaAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(replicaId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(replicaId);
            if (current is null)
                return ReplicaOperationResult.NotFound(replicaId);

            if (current.Status is not (ReplicaStatus.Healthy or ReplicaStatus.Lagging))
                return ReplicaOperationResult.Rejected($"Cannot suspend replica in status '{current.Status}'.");

            routing.RemoveReplica(current.DatabaseId, current.NodeId!);
            var suspended = current with { Status = ReplicaStatus.Suspended };
            Store(suspended);
            return ReplicaOperationResult.Ok(suspended);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>Wake-on-connect resume (FR7): re-registers the replica in the routing table, same-node-first.</summary>
    public async Task<ReplicaOperationResult> ResumeReplicaAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(replicaId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(replicaId);
            if (current is null)
                return ReplicaOperationResult.NotFound(replicaId);

            if (current.Status != ReplicaStatus.Suspended)
                return ReplicaOperationResult.Rejected($"Cannot resume replica in status '{current.Status}'; must be Suspended.");

            var requiredTier = current.ComputeSize.RequiredTier();
            var nodeId = current.NodeId!;
            if (!NodeStillQualifies(nodeId, current.Region, requiredTier))
            {
                var node = placement.SelectNode(current.Region, requiredTier, excludeNodeIds: [nodeId]);
                if (node is null)
                    return ReplicaOperationResult.NoQualifyingNode();
                nodeId = node.NodeId;
                routing.AddOrUpdateReplica(current.DatabaseId, new ComputeEndpoint(node.NodeId, node.Host, node.Port));
            }
            else
            {
                var node = placement.GetNode(nodeId)!;
                routing.AddOrUpdateReplica(current.DatabaseId, new ComputeEndpoint(node.NodeId, node.Host, node.Port));
            }

            var resumed = current with { Status = ReplicaStatus.Healthy, NodeId = nodeId };
            Store(resumed);
            return ReplicaOperationResult.Ok(resumed);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>Deletion (FR8): removes only this replica's compute assignment and routing entry; the primary and shared page data are untouched.</summary>
    public async Task<ReplicaOperationResult> DeleteReplicaAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(replicaId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(replicaId);
            if (current is null)
                return ReplicaOperationResult.NotFound(replicaId);

            if (current.Status is ReplicaStatus.Deleting or ReplicaStatus.Deleted)
                return ReplicaOperationResult.Rejected($"Replica '{replicaId}' is already {current.Status.ToString().ToLowerInvariant()}.");

            if (current.NodeId is not null)
                routing.RemoveReplica(current.DatabaseId, current.NodeId);

            var deleted = current with { Status = ReplicaStatus.Deleted, NodeId = null };
            Store(deleted);
            return ReplicaOperationResult.Ok(deleted);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>Replica lag reporting (FR5, FR2): the replica's WAL receiver reports its applied LSN and byte-lag behind the primary's confirmed LSN.</summary>
    public ReplicaOperationResult ReportLag(string replicaId, long replicaLsn, long lagBytes)
    {
        lock (_gate)
        {
            if (!_replicas.TryGetValue(replicaId, out var current))
                return ReplicaOperationResult.NotFound(replicaId);

            var status = current.Status == ReplicaStatus.Suspended
                ? ReplicaStatus.Suspended
                : lagBytes > DefaultLagThresholdBytes ? ReplicaStatus.Lagging : ReplicaStatus.Healthy;

            var updated = current with { ReplicaLsn = replicaLsn, LagBytes = lagBytes, Status = status };
            _replicas[replicaId] = updated;
            return ReplicaOperationResult.Ok(updated);
        }
    }

    /// <summary>
    /// Manual-trigger, minimal-scope promotion (FR9): fences the replica in as the new primary
    /// routing target and retires it from the replicas[] rotation. Full automated,
    /// health-check-driven failover orchestration is explicitly out of scope -- a real deployment
    /// would additionally reuse #92's generation/term fencing against the old primary before
    /// calling this, which this control-plane skeleton does not implement.
    /// </summary>
    public async Task<ReplicaOperationResult> PromoteAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(replicaId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(replicaId);
            if (current is null)
                return ReplicaOperationResult.NotFound(replicaId);

            if (current.Status != ReplicaStatus.Healthy)
                return ReplicaOperationResult.Rejected($"Cannot promote replica in status '{current.Status}'; must be Healthy.");

            var node = placement.GetNode(current.NodeId!)
                ?? throw new InvalidOperationException($"Replica '{replicaId}' has no resolvable compute node.");

            routing.RemoveReplica(current.DatabaseId, current.NodeId!);
            routing.UpsertRoute(current.DatabaseId, new ComputeEndpoint(node.NodeId, node.Host, node.Port), DefaultMaxConnections);

            var promoted = current with { Status = ReplicaStatus.Promoted };
            Store(promoted);
            return ReplicaOperationResult.Ok(promoted);
        }
        finally
        {
            lease.Release();
        }
    }
}
