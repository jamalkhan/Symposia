using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Quorum;

namespace Symposia.BlobStorage.Gateway.Placement;

/// <summary>A rule placement could not fully honour, recorded with the blob (#8 FR2.3 placement constraint gap).</summary>
public sealed record PlacementGap(string Rule, string Detail);

/// <summary>Nodes chosen for a write or repair, and any soft rules bent to choose them.</summary>
public sealed record PlacementPlan(IReadOnlyList<NodeConnection> Targets, IReadOnlyList<PlacementGap> Gaps);

/// <summary>Placement is impossible as required (#8 FR1.4): too few eligible nodes for a hard rule.</summary>
public sealed class PlacementException(string message) : Exception(message);

/// <summary>
/// Chooses the nodes a blob's copies go to (#7 FR3, #8 FR1): the hard per-region counts of its region
/// assignment, then overflow copies spread over other regions. Hard counts that can't be met fail the
/// write with a specific reason rather than silently substituting a region (#8 AC10); overflow and
/// region-spread shortfalls are best effort and come back as <see cref="PlacementGap"/>s. Among equally
/// suitable nodes the one with the most free space goes first, a deliberately simple, swappable heuristic
/// (node selection scoring is its own feature).
/// </summary>
public sealed class PlacementEngine(INodeRegistry nodes, IOptions<GatewayOptions> options)
{
    /// <summary>The requirement for a blob assigned to <paramref name="regions"/>, in this gateway's redundancy mode.</summary>
    public QuorumRequirement Requirement(IReadOnlyList<string> regions, long? sizeBytes = null)
    {
        var o = options.Value;
        return o.Redundancy.Mode == RedundancyMode.Network
            ? QuorumPolicy.Resolve(regions)
            : QuorumPolicy.Bootstrap(nodes.Writable(sizeBytes).Count, o.WriteQuorumCount, o.MinCopiesPerObject);
    }

    /// <summary>
    /// Targets for a new write. Throws <see cref="PlacementException"/> when a hard rule can't be met or
    /// fewer nodes than the write quorum are eligible. Fewer than <see cref="QuorumRequirement.TotalCopies"/>
    /// but at least the quorum is accepted: the caller records the shortfall for repair (#8 FR1.3).
    /// </summary>
    public PlacementPlan PlaceNew(QuorumRequirement requirement, long? sizeBytes)
    {
        var plan = Place(requirement, sizeBytes, existing: [], count: requirement.TotalCopies, newWrite: true);
        if (plan.Targets.Count < requirement.WriteQuorum)
        {
            throw new PlacementException(
                $"Only {plan.Targets.Count} eligible storage node(s) for a write needing a quorum of {requirement.WriteQuorum}.");
        }

        return plan;
    }

    /// <summary>
    /// Nodes for <paramref name="count"/> more copies of a blob already on <paramref name="existing"/>
    /// (repair, pre-replication): region deficits first, then overflow. Never throws; may return fewer.
    /// </summary>
    public PlacementPlan PlaceAdditional(QuorumRequirement requirement, IReadOnlyCollection<NodeConnection> existing, int count, long? sizeBytes,
        IReadOnlyCollection<string>? exclude = null) =>
        Place(requirement, sizeBytes, existing, count, newWrite: false, exclude);

    private PlacementPlan Place(QuorumRequirement requirement, long? sizeBytes, IReadOnlyCollection<NodeConnection> existing, int count,
        bool newWrite, IReadOnlyCollection<string>? exclude = null)
    {
        var taken = new HashSet<string>(existing.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var id in exclude ?? [])
        {
            taken.Add(id);
        }

        // Stage 3+ nodes are offline for placement (#8 FR3.1); lower stages sort after healthy peers.
        var candidates = nodes.Writable(sizeBytes).Where(n => !taken.Contains(n.Id) && n.PenaltyStage < 3)
            .OrderBy(n => n.PenaltyStage)
            .ThenByDescending(n => n.LastProbe?.AvailableStorageBytes ?? 0).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        if (!requirement.EnforcesRegions)
        {
            return new PlacementPlan(candidates.Take(count).ToList(), []);
        }

        var chosen = new List<NodeConnection>();
        var gaps = new List<PlacementGap>();
        var placed = existing.ToList();
        bool InRegion(NodeConnection n, string region) => string.Equals(n.Region, region, StringComparison.OrdinalIgnoreCase);

        // Takes the best node from pool under the fault-domain rules (#8 FR2), or null when the hard rule rules all out.
        NodeConnection? Pick(IEnumerable<NodeConnection> pool)
        {
            var best = pool.Except(chosen)
                .Where(n => n.PublicIp.Length == 0 || !placed.Any(p => p.PublicIp == n.PublicIp)) // hard: never a shared public IP
                .Select(n => (Node: n, Conflicts: SoftConflicts(n, placed)))
                .OrderBy(c => c.Conflicts.Count)
                .FirstOrDefault();
            if (best.Node is null)
            {
                return null;
            }

            foreach (var rule in best.Conflicts)
            {
                // Soft rules bend only when no compliant node exists, and every bend is recorded (#8 FR2.3).
                gaps.Add(new PlacementGap(rule, $"{best.Node.Id} shares its {rule} with another copy; no compliant node was available"));
            }

            chosen.Add(best.Node);
            placed.Add(best.Node);
            return best.Node;
        }

        // 1. Hard per-region counts.
        foreach (var (region, wanted) in requirement.PlacementPerRegion)
        {
            var need = Math.Min(wanted - placed.Count(n => InRegion(n, region)), count - chosen.Count);
            var got = 0;
            while (got < need && Pick(candidates.Where(n => InRegion(n, region))) is not null)
            {
                got++;
            }

            if (got < need)
            {
                if (newWrite)
                {
                    throw new PlacementException(
                        $"Region '{region}' needs {wanted} copies but only {got + wanted - need} eligible node(s) with distinct public IPs are there.");
                }

                gaps.Add(new PlacementGap("region", $"region '{region}' is short {need - got} copy(ies)"));
            }
        }

        // 2. Overflow: outside the target regions, a new region each time while one is available.
        var targets = requirement.Regions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (chosen.Count < count)
        {
            var usedRegions = placed.Select(n => n.Region).Where(r => r.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Pick(candidates.Where(n => n.Region.Length > 0 && !targets.Contains(n.Region) && !usedRegions.Contains(n.Region))) is not null)
            {
                continue;
            }

            var next = Pick(candidates);
            if (next is null)
            {
                break;
            }

            if (requirement.PlacementPerRegion.Count > 0)
            {
                // No fresh region left: the overflow copy shares one (P3 later moves it when a region appears).
                gaps.Add(new PlacementGap("overflow-region", $"overflow copy on {next.Id} shares region '{next.Region}' with another copy"));
            }
        }

        var spread = placed.Select(n => n.Region).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (spread < requirement.MinDistinctRegions && placed.Count >= requirement.MinDistinctRegions)
        {
            gaps.Add(new PlacementGap("region-spread", $"copies span {spread} region(s), want {requirement.MinDistinctRegions}"));
        }

        if (chosen.Count < count)
        {
            gaps.Add(new PlacementGap("capacity", $"placed {chosen.Count} of {count} copies; repair adds the rest as nodes become eligible"));
        }

        return new PlacementPlan(chosen, gaps);
    }

    /// <summary>The soft fault domains (subnet, ASN, facility) <paramref name="node"/> shares with a placed copy; unknown never conflicts.</summary>
    internal static IReadOnlyList<string> SoftConflicts(NodeConnection node, IReadOnlyCollection<NodeConnection> placed)
    {
        var rules = new List<string>();
        if (node.Subnet.Length > 0 && placed.Any(p => p.Subnet == node.Subnet)) rules.Add("subnet");
        if (node.Asn != 0 && placed.Any(p => p.Asn == node.Asn)) rules.Add("asn");
        if (node.Facility.Length > 0 && placed.Any(p => p.Facility == node.Facility)) rules.Add("facility");
        return rules;
    }
}
