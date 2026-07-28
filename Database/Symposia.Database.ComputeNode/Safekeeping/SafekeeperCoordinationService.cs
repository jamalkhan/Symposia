namespace Symposia.Database.ComputeNode.Safekeeping;

/// <summary>
/// New stateless-by-design control-plane service from the #94 architectural plan: owns initial
/// safekeeper peer assignment at provisioning time (called by #95) and mid-flight reassignment on
/// an RTT breach or peer failure. Both trigger sources drive the same replace-a-peer state
/// machine ("one state machine, two trigger sources" -- Arch). State here is in-process for the
/// daemon's local surface; a horizontally-scaled deployment would back this with durable storage
/// per the Arch's non-functional note, out of scope for this issue's control-plane skeleton.
/// </summary>
public sealed class SafekeeperCoordinationService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SafekeeperAssignment> _assignments = [];
    private readonly Dictionary<string, long> _lagBytes = [];
    private readonly TimeProvider _timeProvider;

    public SafekeeperCoordinationService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Initial peer assignment at database provisioning time (FR2, called by #95).</summary>
    public AssignSafekeepersResult AssignInitialPeers(
        string databaseId,
        string primaryNodeId,
        string region,
        IEnumerable<SafekeeperCandidate> candidates)
    {
        lock (_gate)
        {
            var exclude = new HashSet<string> { primaryNodeId };
            var qualifying = SafekeeperEligibility.SelectQualifying(candidates, region, count: 2, exclude);

            if (qualifying.Count < 2)
                return AssignSafekeepersResult.InsufficientPeers();

            var assignment = new SafekeeperAssignment(
                databaseId,
                primaryNodeId,
                qualifying.Select(c => c.NodeId).ToList(),
                region,
                _timeProvider.GetUtcNow(),
                QuorumHealth.Healthy);

            _assignments[databaseId] = assignment;
            return AssignSafekeepersResult.Ok(assignment);
        }
    }

    public SafekeeperAssignment? GetAssignment(string databaseId)
    {
        lock (_gate)
        {
            return _assignments.GetValueOrDefault(databaseId);
        }
    }

    /// <summary>
    /// Replaces <paramref name="degradedPeerNodeId"/> in the assigned peer set, whether the trigger
    /// was a sustained RTT breach (FR3) or a peer crash/partition/#91 penalty removal (FR9). The
    /// primary itself is never touched by this method -- #92 owns primary migration separately.
    /// </summary>
    public AssignSafekeepersResult ReassignPeer(
        string databaseId,
        string degradedPeerNodeId,
        IEnumerable<SafekeeperCandidate> candidates)
    {
        lock (_gate)
        {
            if (!_assignments.TryGetValue(databaseId, out var current))
                throw new InvalidOperationException($"No safekeeper assignment exists for database '{databaseId}'.");

            if (!current.PeerNodeIds.Contains(degradedPeerNodeId))
                return AssignSafekeepersResult.Ok(current); // no-op: not a currently assigned peer

            var exclude = new HashSet<string>(current.PeerNodeIds) { current.PrimaryNodeId };
            var replacement = SafekeeperEligibility.SelectQualifying(candidates, current.Region, count: 1, exclude);

            if (replacement.Count == 0)
            {
                var degraded = current with { Status = QuorumHealth.AwaitingQualifyingPeer };
                _assignments[databaseId] = degraded;
                return AssignSafekeepersResult.InsufficientPeers(degraded);
            }

            var newPeers = current.PeerNodeIds
                .Where(id => id != degradedPeerNodeId)
                .Append(replacement[0].NodeId)
                .ToList();

            var updated = current with
            {
                PeerNodeIds = newPeers,
                AssignedAt = _timeProvider.GetUtcNow(),
                Status = QuorumHealth.Healthy,
            };
            _assignments[databaseId] = updated;
            return AssignSafekeepersResult.Ok(updated);
        }
    }

    /// <summary>Records a peer's self-reported WAL lag (bytes), feeding #91's Stage 1 trigger metric (FR11).</summary>
    public void ReportLagBytes(string databaseId, long lagBytes)
    {
        lock (_gate)
        {
            _lagBytes[databaseId] = lagBytes;
        }
    }

    public long GetLagBytes(string databaseId)
    {
        lock (_gate)
        {
            return _lagBytes.GetValueOrDefault(databaseId);
        }
    }
}
