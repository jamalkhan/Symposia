namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// The single "find me a qualifying node" primitive shared by provisioning, resize-triggered
/// reassignment (this issue), and #92's migration flow, per the #95 architectural plan's explicit
/// call to avoid three drifting copies of region/capacity/tier filtering logic.
/// </summary>
public interface IComputeNodePlacementService
{
    NodeCandidate? SelectNode(string region, int requiredTier, IReadOnlyCollection<string> excludeNodeIds);

    NodeCandidate? GetNode(string nodeId);
}

/// <summary>In-memory candidate pool for this control-plane skeleton; a real deployment reads #90's live capacity ledger.</summary>
public sealed class InMemoryComputeNodePlacementService : IComputeNodePlacementService
{
    private readonly Lock _gate = new();
    private readonly List<NodeCandidate> _candidates = [];

    public void Register(NodeCandidate candidate)
    {
        lock (_gate)
        {
            _candidates.RemoveAll(c => c.NodeId == candidate.NodeId);
            _candidates.Add(candidate);
        }
    }

    public NodeCandidate? SelectNode(string region, int requiredTier, IReadOnlyCollection<string> excludeNodeIds)
    {
        lock (_gate)
        {
            return _candidates
                .Where(c => c.Region == region && c.Tier <= requiredTier && c.AvailableCapacity > 0 && !excludeNodeIds.Contains(c.NodeId))
                .OrderBy(c => c.Tier) // prefer the lowest-tier node that still qualifies, per the arch's bin-packing note
                .FirstOrDefault();
        }
    }

    public NodeCandidate? GetNode(string nodeId)
    {
        lock (_gate)
        {
            return _candidates.FirstOrDefault(c => c.NodeId == nodeId);
        }
    }
}
