using Symposia.Database.ComputeNode.Proxy;

namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>Who caused a Compute Attachment Swap (issue #102, FR3.3/AC7 audit trail).</summary>
public enum SwapTrigger
{
    Tenant,
    /// <summary>Not fired by anything in this pass -- FR3/EOL enforcement is explicitly out of scope here -- but the audit schema supports it so a future sweep job needs no schema change.</summary>
    EolEnforced,
}

public enum SwapStatus
{
    InProgress,
    /// <summary>Cutover complete; the old node is retained, proxy-unreachable, for the FR2.4 rollback grace window.</summary>
    AwaitingRollbackWindow,
    /// <summary>Grace window elapsed without rollback; the old node is released back to capacity.</summary>
    Completed,
    RolledBack,
    Failed,
}

/// <summary>
/// One Compute Attachment Swap (issue #102, FR2.1-FR2.5): a major-version upgrade implemented as a
/// new compute-node attachment against the tenant's unchanged storage, not a data migration. Doubles
/// as the FR3.3/AC7 audit row -- one record per swap, its <see cref="Status"/> mutated in place as the
/// swap progresses, never re-created.
/// </summary>
public sealed record SwapRecord(
    string SwapId,
    string DatabaseId,
    int FromMajor,
    int ToMajor,
    SwapTrigger Trigger,
    SwapStatus Status,
    DateTimeOffset InitiatedAt,
    string OldPrimaryNodeId,
    string? NewPrimaryNodeId,
    DateTimeOffset? CutoverCompletedAt = null,
    DateTimeOffset? GraceWindowExpiresAt = null,
    DateTimeOffset? RolledBackAt = null,
    string? FailureReason = null);

public enum SwapOperationOutcome
{
    Accepted,
    RolledBack,
    NotFound,
    /// <summary>Another swap is already in flight or in its rollback grace window for this database (the #102 Arch's concurrency rule).</summary>
    Conflict,
    /// <summary>FR2.5/AC4: rejected before any node was touched -- distinguished from validation failures per the Arch's API section.</summary>
    NoRegionalCapacity,
    /// <summary>Target major isn't in the platform's currently supported set, or equals the database's current major -- a request-shape problem, not a capacity one.</summary>
    UnsupportedMajorVersion,
    RollbackWindowExpired,
}

public sealed record SwapOperationResult(SwapOperationOutcome Outcome, SwapRecord? Swap, string? Reason)
{
    public static SwapOperationResult Accepted(SwapRecord swap) => new(SwapOperationOutcome.Accepted, swap, null);

    public static SwapOperationResult RolledBack(SwapRecord swap) => new(SwapOperationOutcome.RolledBack, swap, null);

    public static SwapOperationResult NotFound(string reason) => new(SwapOperationOutcome.NotFound, null, reason);

    public static SwapOperationResult Conflict(SwapRecord activeSwap) =>
        new(SwapOperationOutcome.Conflict, activeSwap, $"A swap ('{activeSwap.SwapId}', status '{activeSwap.Status}') is already in flight or in its rollback grace window for database '{activeSwap.DatabaseId}'.");

    public static SwapOperationResult NoRegionalCapacity(int targetMajor, string region) =>
        new(SwapOperationOutcome.NoRegionalCapacity, null, $"No compute node operator in region '{region}' has declared support for Postgres major version {targetMajor}.");

    public static SwapOperationResult UnsupportedMajorVersion(string reason) =>
        new(SwapOperationOutcome.UnsupportedMajorVersion, null, reason);

    public static SwapOperationResult RollbackWindowExpired(string reason) =>
        new(SwapOperationOutcome.RollbackWindowExpired, null, reason);
}

/// <summary>
/// Implements the #102 architectural plan's "Compute Attachment Swap" primitive: a per-database FSM
/// that provisions a new compute node on a target Postgres major against the tenant's existing
/// safekeeper/blob-storage data, re-forms a fresh safekeeper quorum on the new node, cuts the proxy
/// routing entry over, and retains the old node reference for a rollback grace window rather than
/// destroying it immediately. One primitive, two callers in this pass (tenant-initiated upgrade; the
/// <see cref="SwapTrigger.EolEnforced"/> trigger is schema-only until #103/EOL-enforcement lands).
///
/// Concurrency rule (per the Arch plan): only one swap may be in flight or in its rollback-grace-window
/// per database at a time, enforced with a per-database lease exactly like <see cref="DatabaseLifecycleOrchestrator"/>'s;
/// a second request while one is active is rejected with 409/<see cref="SwapOperationOutcome.Conflict"/>.
/// </summary>
public sealed class ComputeAttachmentSwapOrchestrator(
    DatabaseLifecycleOrchestrator lifecycle,
    IComputeNodePlacementService placement,
    ISafekeeperAssignmentClient safekeepers,
    IProxyRoutingClient routing,
    IPostgresVersionCatalog versionCatalog,
    TimeProvider? timeProvider = null,
    TimeSpan? rollbackGraceWindow = null)
{
    private const int DefaultMaxConnections = 200;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SemaphoreSlim> _leases = [];
    private readonly Dictionary<string, SwapRecord> _swapsById = [];
    private readonly Dictionary<string, string> _activeSwapIdByDatabase = []; // databaseId -> swapId currently in-flight or in-grace-window
    private readonly List<SwapRecord> _auditLog = [];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _rollbackGraceWindow = rollbackGraceWindow ?? TimeSpan.FromHours(24);

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

    private void StoreSwap(SwapRecord record)
    {
        lock (_gate)
        {
            _swapsById[record.SwapId] = record;
        }
    }

    private void Audit(SwapRecord record)
    {
        lock (_gate)
        {
            var index = _auditLog.FindIndex(r => r.SwapId == record.SwapId);
            if (index >= 0)
                _auditLog[index] = record;
            else
                _auditLog.Add(record);
        }
    }

    public SwapRecord? GetSwap(string swapId)
    {
        lock (_gate)
        {
            return _swapsById.GetValueOrDefault(swapId);
        }
    }

    /// <summary>FR3.3/AC7: the full, queryable audit trail for a database's swaps (tenant-initiated and, eventually, EOL-enforced), newest first.</summary>
    public IReadOnlyList<SwapRecord> GetAuditRecords(string databaseId)
    {
        lock (_gate)
        {
            return [.. _auditLog.Where(r => r.DatabaseId == databaseId).OrderByDescending(r => r.InitiatedAt)];
        }
    }

    /// <summary>
    /// Returns the database's active swap (in flight or still in its rollback grace window), or null
    /// if none. Lazily finalizes (moves to <see cref="SwapStatus.Completed"/> and releases the
    /// concurrency slot for) a swap whose grace window has elapsed -- there is no background sweep in
    /// this pass, so expiry is evaluated on next access.
    /// </summary>
    private SwapRecord? GetActiveSwap(string databaseId)
    {
        lock (_gate)
        {
            if (!_activeSwapIdByDatabase.TryGetValue(databaseId, out var swapId))
                return null;

            var swap = _swapsById[swapId];
            if (swap.Status == SwapStatus.AwaitingRollbackWindow && swap.GraceWindowExpiresAt is not null && _timeProvider.GetUtcNow() >= swap.GraceWindowExpiresAt)
            {
                var completed = swap with { Status = SwapStatus.Completed };
                _swapsById[swapId] = completed;
                var auditIndex = _auditLog.FindIndex(r => r.SwapId == swapId);
                if (auditIndex >= 0)
                    _auditLog[auditIndex] = completed;
                _activeSwapIdByDatabase.Remove(databaseId);
                return null;
            }

            return swap;
        }
    }

    /// <summary>
    /// Tenant-initiated (or, in a future pass, EOL-enforced) major-version upgrade (FR2.1-FR2.5,
    /// AC3-AC5). Fails fast on the capacity precondition before touching any node (FR2.5/AC4), then
    /// provisions a new compute node on the target major, re-forms a fresh safekeeper quorum against
    /// the same existing storage, and cuts the proxy routing entry over.
    /// </summary>
    public async Task<SwapOperationResult> UpgradeAsync(string databaseId, int targetMajor, SwapTrigger trigger = SwapTrigger.Tenant, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(databaseId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var current = lifecycle.GetState(databaseId);
            if (current is null)
                return SwapOperationResult.NotFound($"No database '{databaseId}' exists.");

            if (current.State != LifecycleState.Active)
                return SwapOperationResult.UnsupportedMajorVersion($"Cannot upgrade database '{databaseId}' in state '{current.State}'; must be Active.");

            var activeSwap = GetActiveSwap(databaseId);
            if (activeSwap is not null)
                return SwapOperationResult.Conflict(activeSwap);

            if (!versionCatalog.IsSupported(targetMajor))
                return SwapOperationResult.UnsupportedMajorVersion($"Postgres major version {targetMajor} is not currently supported by the platform.");

            if (targetMajor == current.PostgresMajorVersion)
                return SwapOperationResult.UnsupportedMajorVersion($"Database '{databaseId}' is already on major version {targetMajor}.");

            // FR2.5/AC4: capacity precondition checked, and rejected, before any node is touched.
            var requiredTier = current.ComputeSize.RequiredTier();
            var node = placement.SelectNode(current.Region, requiredTier, excludeNodeIds: [current.PrimaryNodeId!], requiredPostgresMajor: targetMajor);
            if (node is null)
                return SwapOperationResult.NoRegionalCapacity(targetMajor, current.Region);

            var swapId = Guid.NewGuid().ToString("n");
            var initiatedAt = _timeProvider.GetUtcNow();
            var inProgress = new SwapRecord(
                swapId, databaseId, current.PostgresMajorVersion, targetMajor, trigger, SwapStatus.InProgress,
                initiatedAt, OldPrimaryNodeId: current.PrimaryNodeId!, NewPrimaryNodeId: node.NodeId);
            StoreSwap(inProgress);
            Audit(inProgress);
            lock (_gate)
            {
                _activeSwapIdByDatabase[databaseId] = swapId;
            }

            // Step 4 of the Arch plan: the new-major node joins as a new WAL-streaming primary and
            // negotiates its own fresh safekeeper quorum -- never a reuse of the old node's peers.
            var peerIds = await safekeepers.AssignSafekeepersAsync(databaseId, node.NodeId, current.Region, cancellationToken);

            var applyResult = await lifecycle.ApplyComputeAttachmentSwapAsync(databaseId, node.NodeId, peerIds, targetMajor, cancellationToken);
            if (applyResult.Outcome != LifecycleOperationOutcome.Ok)
            {
                var failed = inProgress with { Status = SwapStatus.Failed, FailureReason = applyResult.Reason };
                StoreSwap(failed);
                Audit(failed);
                lock (_gate)
                {
                    _activeSwapIdByDatabase.Remove(databaseId);
                }
                return SwapOperationResult.UnsupportedMajorVersion(applyResult.Reason ?? "Failed to apply the compute attachment swap.");
            }

            // Step 5: cut the proxy routing entry over to the new node.
            routing.UpsertRoute(databaseId, new ComputeEndpoint(node.NodeId, node.Host, node.Port), DefaultMaxConnections);

            var cutoverAt = _timeProvider.GetUtcNow();
            var awaitingRollback = inProgress with
            {
                Status = SwapStatus.AwaitingRollbackWindow,
                CutoverCompletedAt = cutoverAt,
                GraceWindowExpiresAt = cutoverAt + _rollbackGraceWindow,
            };
            StoreSwap(awaitingRollback);
            Audit(awaitingRollback);

            return SwapOperationResult.Accepted(awaitingRollback);
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>
    /// FR2.4/AC5: reverts a swap to its prior major within the rollback grace window by re-attaching
    /// the retained old-major compute node (never destroyed, never mutated) and re-forming its own
    /// fresh safekeeper quorum -- a second Compute Attachment Swap in the reverse direction, per the
    /// Arch plan, not a special-cased recovery path.
    /// </summary>
    public async Task<SwapOperationResult> RollbackAsync(string databaseId, string swapId, CancellationToken cancellationToken = default)
    {
        var lease = LeaseFor(databaseId);
        await lease.WaitAsync(cancellationToken);
        try
        {
            var swap = GetSwap(swapId);
            if (swap is null || swap.DatabaseId != databaseId)
                return SwapOperationResult.NotFound($"No swap '{swapId}' exists for database '{databaseId}'.");

            if (swap.Status != SwapStatus.AwaitingRollbackWindow)
                return SwapOperationResult.RollbackWindowExpired($"Swap '{swapId}' is not eligible for rollback (status '{swap.Status}').");

            if (swap.GraceWindowExpiresAt is not null && _timeProvider.GetUtcNow() >= swap.GraceWindowExpiresAt)
            {
                var expired = swap with { Status = SwapStatus.Completed };
                StoreSwap(expired);
                Audit(expired);
                lock (_gate)
                {
                    _activeSwapIdByDatabase.Remove(databaseId);
                }
                return SwapOperationResult.RollbackWindowExpired($"The rollback grace window for swap '{swapId}' has elapsed; the new-major compute node is now authoritative.");
            }

            var current = lifecycle.GetState(databaseId);
            if (current is null)
                return SwapOperationResult.NotFound($"No database '{databaseId}' exists.");

            var peerIds = await safekeepers.AssignSafekeepersAsync(databaseId, swap.OldPrimaryNodeId, current.Region, cancellationToken);

            var applyResult = await lifecycle.ApplyComputeAttachmentSwapAsync(databaseId, swap.OldPrimaryNodeId, peerIds, swap.FromMajor, cancellationToken);
            if (applyResult.Outcome != LifecycleOperationOutcome.Ok)
                return SwapOperationResult.UnsupportedMajorVersion(applyResult.Reason ?? "Failed to roll back the compute attachment swap.");

            var oldNode = placement.GetNode(swap.OldPrimaryNodeId);
            if (oldNode is not null)
                routing.UpsertRoute(databaseId, new ComputeEndpoint(oldNode.NodeId, oldNode.Host, oldNode.Port), DefaultMaxConnections);

            var rolledBack = swap with { Status = SwapStatus.RolledBack, RolledBackAt = _timeProvider.GetUtcNow() };
            StoreSwap(rolledBack);
            Audit(rolledBack);
            lock (_gate)
            {
                _activeSwapIdByDatabase.Remove(databaseId);
            }

            return SwapOperationResult.RolledBack(rolledBack);
        }
        finally
        {
            lease.Release();
        }
    }
}
