namespace Symposia.Database.ComputeNode.Safekeeping;

/// <summary>
/// Self-reported sustained RTT breach (rolling-window average above the 5ms budget for a
/// confirmation window, to avoid flapping on transient jitter -- Arch) sent out-of-band by a
/// safekeeper peer, rather than waiting on the slower periodic metrics cadence.
/// </summary>
public sealed record RttBreachReport(string DatabaseId, string DegradedPeerNodeId, SafekeeperCandidate[] Candidates);
