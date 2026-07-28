namespace Symposia.Database.ComputeNode.Safekeeping;

public enum AssignSafekeepersOutcome
{
    Assigned,
    InsufficientQualifyingPeers,
}

/// <summary>
/// Result of assigning or reassigning safekeeper peers. <see cref="AssignSafekeepersOutcome.InsufficientQualifyingPeers"/>
/// is the spec's "fail closed" case (TC-14): the primary cannot achieve a full 3-node quorum, and
/// this must be surfaced/alerted rather than silently falling back to a non-qualifying peer or an
/// under-sized quorum.
/// </summary>
public sealed record AssignSafekeepersResult(AssignSafekeepersOutcome Outcome, SafekeeperAssignment? Assignment, string? Reason)
{
    public static AssignSafekeepersResult Ok(SafekeeperAssignment assignment) =>
        new(AssignSafekeepersOutcome.Assigned, assignment, Reason: null);

    public static AssignSafekeepersResult InsufficientPeers(SafekeeperAssignment? degradedAssignment = null) =>
        new(
            AssignSafekeepersOutcome.InsufficientQualifyingPeers,
            degradedAssignment,
            "No qualifying safekeeper peer is available (region + RTT + NVMe + non-penalized constraints).");
}
