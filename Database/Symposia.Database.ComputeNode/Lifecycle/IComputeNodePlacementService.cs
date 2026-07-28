namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// The single "find me a qualifying node" primitive shared by provisioning, resize-triggered
/// reassignment (this issue), and #92's migration flow, per the #95 architectural plan's explicit
/// call to avoid three drifting copies of region/capacity/tier filtering logic.
/// </summary>
public interface IComputeNodePlacementService
{
    /// <summary>
    /// <paramref name="requiredPostgresMajor"/> is the version-aware filter added by issue #102
    /// (FR1.5/FR4.2): when supplied, only nodes that have declared support for that major are
    /// eligible; <c>null</c> preserves the pre-#102 behavior of ignoring major version entirely.
    /// <paramref name="requiredExtensions"/> is the extension-aware filter added by issue #103
    /// (FR3.1-FR3.3): when supplied and non-empty, only nodes whose declared extension set is a
    /// superset are eligible; additive to the postgres-major filter, not a replacement for it.
    /// </summary>
    NodeCandidate? SelectNode(string region, int requiredTier, IReadOnlyCollection<string> excludeNodeIds, int? requiredPostgresMajor = null, IReadOnlyCollection<string>? requiredExtensions = null);

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

    public NodeCandidate? SelectNode(string region, int requiredTier, IReadOnlyCollection<string> excludeNodeIds, int? requiredPostgresMajor = null, IReadOnlyCollection<string>? requiredExtensions = null)
    {
        lock (_gate)
        {
            return _candidates
                .Where(c => c.Region == region && c.Tier <= requiredTier && c.AvailableCapacity > 0 && !excludeNodeIds.Contains(c.NodeId))
                .Where(c => requiredPostgresMajor is null || c.SupportsPostgresMajor(requiredPostgresMajor.Value))
                .Where(c => requiredExtensions is null || c.DeclaresExtensions(requiredExtensions))
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
