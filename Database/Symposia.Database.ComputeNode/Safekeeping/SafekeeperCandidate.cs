namespace Symposia.Database.ComputeNode.Safekeeping;

/// <summary>
/// A node's currently-known standing as a candidate safekeeper peer, sourced from #89's verified
/// hardware/tier data and continuous RTT monitoring -- never self-declared by the candidate itself
/// (Arch: "the coordination service treats #89 as the source of truth").
/// </summary>
public sealed record SafekeeperCandidate(
    string NodeId,
    string Region,
    double RttMs,
    bool HasNvmeDurableStorage,
    bool IsPenalized);
