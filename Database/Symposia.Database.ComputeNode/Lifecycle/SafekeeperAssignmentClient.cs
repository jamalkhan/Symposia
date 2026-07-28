using Symposia.Database.ComputeNode.Safekeeping;

namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>Adapts #94's <see cref="SafekeeperCoordinationService"/> for in-process use by the #95 orchestrator.</summary>
public sealed class SafekeeperAssignmentClient(SafekeeperCoordinationService coordination) : ISafekeeperAssignmentClient
{
    public Task<IReadOnlyList<string>> AssignSafekeepersAsync(string databaseId, string primaryNodeId, string region, CancellationToken cancellationToken)
    {
        // #94 provides no candidate pool of its own; the #95 arch plan flags this as an assumption
        // on #94's own upcoming interface. A real deployment sources candidates from the same
        // region-scoped compute-node registry #90 already maintains.
        var result = coordination.AssignInitialPeers(databaseId, primaryNodeId, region, candidates: []);
        IReadOnlyList<string> peers = result.Assignment?.PeerNodeIds ?? [];
        return Task.FromResult(peers);
    }
}
