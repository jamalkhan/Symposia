namespace Symposia.Database.ComputeNode.Safekeeping;

/// <summary>Request body for initial safekeeper assignment (called by #95 at provisioning time).</summary>
public sealed record AssignSafekeepersRequest(string PrimaryNodeId, string Region, SafekeeperCandidate[] Candidates);

/// <summary>Request body to trigger/record a peer swap, whether from an RTT breach or a peer failure/crash.</summary>
public sealed record ReassignSafekeeperRequest(string DegradedPeerNodeId, SafekeeperCandidate[] Candidates);
