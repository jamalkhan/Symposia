using Symposia.Database.ComputeNode.Proxy;

namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// Single-writer control-plane FSM for the #95 database lifecycle: provisioning, resize,
/// idle-triggered suspend, wake-on-connect resume, and soft-delete. Per the Arch plan, every
/// transition is serialized behind a per-database lease (a semaphore standing in for the
/// data-store CAS the plan describes) so a resize racing an idle-suspend evaluation is always
/// either applied strictly in order or rejected against the state the loser actually observes --
/// never left half-applied.
/// </summary>
public sealed class DatabaseLifecycleOrchestrator(
    IComputeNodePlacementService placement,
    IBlobBucketProvisioner bucketProvisioner,
    ISafekeeperAssignmentClient safekeepers,
    IProxyRoutingClient routing,
    TimeProvider? timeProvider = null,
    IPostgresVersionCatalog? versionCatalog = null)
{
    private const int DefaultMaxConnections = 200;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DatabaseLifecycleRecord> _records = [];
    private readonly Dictionary<string, SemaphoreSlim> _leases = [];
    private readonly Dictionary<string, DateTimeOffset> _idleSince = [];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IPostgresVersionCatalog _versionCatalog = versionCatalog ?? new InMemoryPostgresVersionCatalog();

    public DatabaseLifecycleRecord? GetState(string databaseId)
    {
        lock (_gate)
        {
            return _records.GetValueOrDefault(databaseId);
        }
    }

    private SemaphoreSlim LeaseFor(string databaseId)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(databaseId, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _leases[databaseId] = semaphore;
            }
            return semaphore;
        }
    }

    private void Store(DatabaseLifecycleRecord record)
    {
        lock (_gate)
        {
            _records[record.DatabaseId] = record with { StateVersion = record.StateVersion + 1 };
        }
    }

    /// <summary>
    /// Fan-out/fan-in provisioning (FR1-6): node selection, bucket provisioning, and safekeeper
    /// assignment run concurrently since none depends on the others; the proxy route write and
    /// connection-string issuance are the two steps that must wait on all three, per the Arch's
    /// 30-second-budget rationale.
    /// </summary>
    public async Task<LifecycleOperationResult> ProvisionAsync(ProvisionDatabaseRequest request, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(request.DatabaseId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            if (_records.ContainsKey(request.DatabaseId))
                return LifecycleOperationResult.Rejected($"Database '{request.DatabaseId}' already exists.");

            // FR1.4/AC2: default to the current latest supported major when the tenant doesn't
            // specify one; an explicit selection must be in the currently supported set.
            var postgresMajorVersion = request.PostgresMajorVersion ?? _versionCatalog.LatestSupportedMajor;
            if (!_versionCatalog.IsSupported(postgresMajorVersion))
                return LifecycleOperationResult.UnsupportedMajorVersion(postgresMajorVersion);

            var provisioning = new DatabaseLifecycleRecord(
                request.DatabaseId, request.Region, LifecycleState.Provisioning, StateVersion: 0,
                request.ComputeSize, PrimaryNodeId: null, SafekeeperPeerIds: [], BlobBucketId: null,
                request.IdleSuspendSeconds, ConnectionString: null, PostgresMajorVersion: postgresMajorVersion);
            Store(provisioning);

            var requiredTier = request.ComputeSize.RequiredTier();
            // FR1.5: only route to a node that has declared support for the database's major version.
            var node = placement.SelectNode(request.Region, requiredTier, excludeNodeIds: [], requiredPostgresMajor: postgresMajorVersion);
            if (node is null)
            {
                Store(provisioning with { State = LifecycleState.Failed, FailureReason = "No qualifying compute node available." });
                return LifecycleOperationResult.NoQualifyingNode();
            }

            var bucketTask = bucketProvisioner.ProvisionBucketAsync(request.DatabaseId, cancellationToken);
            var safekeepersTask = safekeepers.AssignSafekeepersAsync(request.DatabaseId, node.NodeId, request.Region, cancellationToken);
            await Task.WhenAll(bucketTask, safekeepersTask);

            var bucketId = await bucketTask;
            var peerIds = await safekeepersTask;

            routing.UpsertRoute(request.DatabaseId, new ComputeEndpoint(node.NodeId, node.Host, node.Port), DefaultMaxConnections);

            var connectionString = $"postgres://{request.DatabaseId}.{request.Region}.db.symposia.network:5432/{request.DatabaseName}";
            var active = provisioning with
            {
                State = LifecycleState.Active,
                PrimaryNodeId = node.NodeId,
                SafekeeperPeerIds = peerIds,
                BlobBucketId = bucketId,
                ConnectionString = connectionString,
            };
            Store(active);
            return LifecycleOperationResult.Ok(active);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>
    /// Compute resize (FR7): a brief restart, never a data migration. Reassigns to a qualifying
    /// node (and re-establishes safekeepers on the new primary) only when the requested size
    /// needs a higher compute tier than the current node supports.
    /// </summary>
    public async Task<LifecycleOperationResult> ResizeAsync(string databaseId, DatabaseSize newSize, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(databaseId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(databaseId);
            if (current is null)
                return LifecycleOperationResult.NotFound(databaseId);

            if (current.State != LifecycleState.Active)
                return LifecycleOperationResult.Rejected($"Cannot resize database in state '{current.State}'; must be Active.", current);

            Store(current with { State = LifecycleState.Resizing });

            var requiredTier = newSize.RequiredTier();
            var needsReassignment = !NodeStillQualifies(current.PrimaryNodeId!, current.Region, requiredTier);

            string primaryNodeId = current.PrimaryNodeId!;
            IReadOnlyList<string> peerIds = current.SafekeeperPeerIds;

            if (needsReassignment)
            {
                var node = placement.SelectNode(current.Region, requiredTier, excludeNodeIds: [current.PrimaryNodeId!]);
                if (node is null)
                {
                    Store(current with { State = LifecycleState.Active }); // remain on original node/size, not half-migrated
                    return LifecycleOperationResult.NoQualifyingNode();
                }

                peerIds = await safekeepers.AssignSafekeepersAsync(databaseId, node.NodeId, current.Region, cancellationToken);
                routing.UpsertRoute(databaseId, new ComputeEndpoint(node.NodeId, node.Host, node.Port), DefaultMaxConnections);
                primaryNodeId = node.NodeId;
            }

            var resized = current with
            {
                State = LifecycleState.Active,
                ComputeSize = newSize,
                PrimaryNodeId = primaryNodeId,
                SafekeeperPeerIds = peerIds,
            };
            Store(resized);
            return LifecycleOperationResult.Ok(resized);
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

    /// <summary>Proxy-reported signal (FR9/FR10): a database's active connection count dropped to zero.</summary>
    public void RecordConnectionCountZero(string databaseId)
    {
        lock (_gate)
        {
            _idleSince[databaseId] = _timeProvider.GetUtcNow();
        }
    }

    /// <summary>Proxy-reported signal: a connection arrived, cancelling any pending idle timer.</summary>
    public void RecordConnectionOpened(string databaseId)
    {
        lock (_gate)
        {
            _idleSince.Remove(databaseId);
        }
    }

    /// <summary>
    /// Background sweep entry point: suspends a database once its zero-connection duration has
    /// reached its configured idle threshold. "Never" (<c>IdleSuspendSeconds is null</c>) means no
    /// timer is ever evaluated.
    /// </summary>
    public async Task<LifecycleOperationResult?> EvaluateIdleAsync(string databaseId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset idleSince;
        lock (_gate)
        {
            if (!_idleSince.TryGetValue(databaseId, out idleSince))
                return null;
        }

        var current = GetState(databaseId);
        if (current is null || current.IdleSuspendSeconds is null)
            return null;

        var elapsed = _timeProvider.GetUtcNow() - idleSince;
        if (elapsed.TotalSeconds < current.IdleSuspendSeconds.Value)
            return null;

        return await SuspendAsync(databaseId, cancellationToken);
    }

    /// <summary>Idle-triggered suspend (FR9): stop Postgres, flush pages, stop compute billing (storage billing is untouched here, out of scope).</summary>
    public async Task<LifecycleOperationResult> SuspendAsync(string databaseId, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(databaseId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(databaseId);
            if (current is null)
                return LifecycleOperationResult.NotFound(databaseId);

            if (current.State != LifecycleState.Active)
                return LifecycleOperationResult.Rejected($"Cannot suspend database in state '{current.State}'; must be Active.", current);

            routing.MarkSuspended(databaseId);
            var suspended = current with { State = LifecycleState.Suspended };
            Store(suspended);
            return LifecycleOperationResult.Ok(suspended);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>
    /// Wake-on-connect resume (FR10), called synchronously by the #93 proxy. Attempts same-node
    /// resume first (the sub-3-second warm-cache path); falls back to fresh placement only if the
    /// original node no longer qualifies.
    /// </summary>
    public async Task<LifecycleOperationResult> ResumeAsync(string databaseId, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(databaseId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(databaseId);
            if (current is null)
                return LifecycleOperationResult.NotFound(databaseId);

            if (current.State != LifecycleState.Suspended)
                return LifecycleOperationResult.Rejected($"Cannot resume database in state '{current.State}'; must be Suspended.", current);

            Store(current with { State = LifecycleState.Resuming });

            var requiredTier = current.ComputeSize.RequiredTier();
            var sameNodeStillQualifies = NodeStillQualifies(current.PrimaryNodeId!, current.Region, requiredTier);

            string primaryNodeId = current.PrimaryNodeId!;
            if (!sameNodeStillQualifies)
            {
                var node = placement.SelectNode(current.Region, requiredTier, excludeNodeIds: [current.PrimaryNodeId!]);
                if (node is null)
                {
                    Store(current with { State = LifecycleState.Failed, FailureReason = "No qualifying compute node available to resume onto." });
                    return LifecycleOperationResult.NoQualifyingNode();
                }
                routing.UpsertRoute(databaseId, new ComputeEndpoint(node.NodeId, node.Host, node.Port), DefaultMaxConnections);
                primaryNodeId = node.NodeId;
            }

            var resumed = current with { State = LifecycleState.Active, PrimaryNodeId = primaryNodeId };
            Store(resumed);
            return LifecycleOperationResult.Ok(resumed);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>
    /// Soft-delete trigger (FR12/13): triggers the standard blob soft-delete mechanism and removes
    /// the compute/proxy assignments immediately; permanent removal after the retention window is
    /// owned entirely by the blob layer's own GC, not this orchestrator.
    /// </summary>
    public async Task<LifecycleOperationResult> DeleteAsync(string databaseId, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(databaseId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(databaseId);
            if (current is null)
                return LifecycleOperationResult.NotFound(databaseId);

            if (current.State is LifecycleState.Deleting or LifecycleState.Deleted)
                return LifecycleOperationResult.Rejected($"Database '{databaseId}' is already {current.State.ToString().ToLowerInvariant()}.", current);

            if (current.State == LifecycleState.Provisioning)
                return LifecycleOperationResult.Rejected("Cannot delete a database that is still provisioning.", current);

            Store(current with { State = LifecycleState.Deleting });

            if (current.BlobBucketId is not null)
                await bucketProvisioner.SoftDeleteBucketAsync(current.BlobBucketId, cancellationToken);

            routing.RemoveRoute(databaseId);

            var deleted = current with { State = LifecycleState.Deleted, PrimaryNodeId = null, SafekeeperPeerIds = [] };
            Store(deleted);
            return LifecycleOperationResult.Ok(deleted);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>
    /// Mutation hook used by the #102 Compute Attachment Swap orchestrator once a swap's cutover has
    /// completed: rewrites the primary node, safekeeper quorum, and Postgres major version onto this
    /// database's lifecycle record. Takes this orchestrator's own per-database lease so it is
    /// serialized against resize/suspend/resume/delete the same as every other transition -- the
    /// swap orchestrator's own lock only protects swap-specific state (concurrency rule / audit),
    /// not the lifecycle record itself.
    /// </summary>
    public async Task<LifecycleOperationResult> ApplyComputeAttachmentSwapAsync(
        string databaseId, string newPrimaryNodeId, IReadOnlyList<string> newSafekeeperPeerIds, int newPostgresMajorVersion, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(databaseId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = GetState(databaseId);
            if (current is null)
                return LifecycleOperationResult.NotFound(databaseId);

            if (current.State != LifecycleState.Active)
                return LifecycleOperationResult.Rejected($"Cannot apply a compute attachment swap while database is in state '{current.State}'; must be Active.", current);

            var updated = current with
            {
                PrimaryNodeId = newPrimaryNodeId,
                SafekeeperPeerIds = newSafekeeperPeerIds,
                PostgresMajorVersion = newPostgresMajorVersion,
            };
            Store(updated);
            return LifecycleOperationResult.Ok(updated);
        }
        finally
        {
            lease.Release();
        }
    }
}
