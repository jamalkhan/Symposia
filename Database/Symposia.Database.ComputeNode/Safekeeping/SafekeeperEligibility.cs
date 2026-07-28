namespace Symposia.Database.ComputeNode.Safekeeping;

/// <summary>
/// Peer eligibility rule for the WAL safekeeper quorum (issue #94, FR2): a candidate must be
/// same-region as the primary, within the RTT latency budget, backed by durable NVMe/SSD storage,
/// and not currently sitting in a #91 penalty stage. All three (plus non-penalized) are required
/// jointly -- meeting two out of three does not qualify a candidate.
/// </summary>
public static class SafekeeperEligibility
{
    /// <summary>Inclusive RTT budget: "&lt;=5ms" per spec, so exactly 5.0ms is eligible.</summary>
    public const double MaxRttMs = 5.0;

    public static bool IsEligible(SafekeeperCandidate candidate, string primaryRegion) =>
        !candidate.IsPenalized
        && candidate.Region == primaryRegion
        && candidate.HasNvmeDurableStorage
        && candidate.RttMs <= MaxRttMs;

    /// <summary>
    /// Selects up to <paramref name="count"/> qualifying candidates for <paramref name="primaryRegion"/>,
    /// excluding any node id in <paramref name="excludeNodeIds"/> (the primary itself and/or peers
    /// already assigned), ordered by RTT ascending so the lowest-latency qualifying peers are
    /// preferred. Returns fewer than <paramref name="count"/> if not enough candidates qualify --
    /// callers must treat a short result as "no qualifying peer available" (spec: fail closed, never
    /// silently fall back to a non-qualifying peer or an under-sized quorum).
    /// </summary>
    public static IReadOnlyList<SafekeeperCandidate> SelectQualifying(
        IEnumerable<SafekeeperCandidate> candidates,
        string primaryRegion,
        int count,
        IReadOnlySet<string>? excludeNodeIds = null)
    {
        return candidates
            .Where(c => (excludeNodeIds is null || !excludeNodeIds.Contains(c.NodeId)) && IsEligible(c, primaryRegion))
            .OrderBy(c => c.RttMs)
            .Take(count)
            .ToList();
    }
}
