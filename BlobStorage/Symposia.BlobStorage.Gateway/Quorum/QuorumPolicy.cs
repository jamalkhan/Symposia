namespace Symposia.BlobStorage.Gateway.Quorum;

/// <summary>
/// What a blob's region assignment demands (#7 FR1/FR3, #8 FR1.2): how many copies to place where, how
/// many durable acks make a quorum, and how many of those acks each target region must contribute.
/// </summary>
/// <param name="TotalCopies">Copies the blob must end up with (#8 Summary Table).</param>
/// <param name="WriteQuorum">Durable acks before the write succeeds (#7 table).</param>
/// <param name="PlacementPerRegion">Hard copy counts per target region; empty for a global blob.</param>
/// <param name="OverflowCopies">Copies outside the target regions, each preferably in a different region.</param>
/// <param name="MinDistinctRegions">Regions the copies should span (global blobs: 3). Best effort; a shortfall is a placement gap.</param>
/// <param name="QuorumPerRegion">Acks each target region must contribute before quorum counts (#7 FR3).</param>
public sealed record QuorumRequirement(
    int TotalCopies,
    int WriteQuorum,
    IReadOnlyDictionary<string, int> PlacementPerRegion,
    int OverflowCopies,
    int MinDistinctRegions,
    IReadOnlyDictionary<string, int> QuorumPerRegion)
{
    public IReadOnlyList<string> Regions => [.. PlacementPerRegion.Keys];

    /// <summary>
    /// The copy count repair maintains (stored per object as <c>target_copies</c>): the total, except in
    /// bootstrap mode, where writes go to every writable node but only the configured floor is enforced.
    /// </summary>
    public int RepairTarget { get; init; } = TotalCopies;

    /// <summary>Whether region placement applies (network mode); bootstrap writes ignore regions.</summary>
    public bool EnforcesRegions { get; init; } = true;

    /// <summary>
    /// Whether these confirmations make a quorum: enough acks in total <em>and</em> each target region's
    /// minimum met. Count alone never suffices for a region-constrained blob (#7 AC3).
    /// </summary>
    public bool IsSatisfiedBy(IEnumerable<string> confirmedRegions)
    {
        var regions = confirmedRegions.ToList();
        return regions.Count >= WriteQuorum && QuorumPerRegion.All(min =>
            regions.Count(r => string.Equals(r, min.Key, StringComparison.OrdinalIgnoreCase)) >= min.Value);
    }

    /// <summary>The same requirement with <paramref name="extra"/> more copies to place (#8 FR3 pre-replication), as overflow.</summary>
    public QuorumRequirement WithExtraCopies(int extra) =>
        extra <= 0 ? this : this with { TotalCopies = TotalCopies + extra, OverflowCopies = OverflowCopies + extra, RepairTarget = RepairTarget + extra };
}

/// <summary>
/// The copy-count and quorum tables of Requirements/BlobStorage/write-quorum-and-consistency.md and
/// redundancy-and-data-integrity.md, as a pure function of the region assignment. Pure so every row,
/// and the generalized four-or-more formula, is table-testable without a cluster (#7 QA TC-1.x).
/// </summary>
public static class QuorumPolicy
{
    /// <summary>Requirement for a blob assigned to <paramref name="regions"/> (empty = global).</summary>
    public static QuorumRequirement Resolve(IReadOnlyList<string> regions)
    {
        var targets = regions.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var none = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return targets.Count switch
        {
            // 4 copies across ≥3 regions, quorum 3.
            0 => new QuorumRequirement(4, 3, none, 4, 3, none),
            // 3 in the region + 1 + 1 in two other regions; ≥2 of the 3 acks from the region.
            1 => new QuorumRequirement(5, 3, Counts((targets[0], 3)), 2, 3, Counts((targets[0], 2))),
            // 3 + 2 + 1 overflow; ≥1 ack from each target region.
            2 => new QuorumRequirement(6, 4, Counts((targets[0], 3), (targets[1], 2)), 1, 3, Counts((targets[0], 1), (targets[1], 1))),
            // 2 + 2 + 2 + 1 overflow; ≥1 ack per target region.
            3 => new QuorumRequirement(7, 4, Counts(targets.Select(t => (t, 2)).ToArray()), 1, 4, Counts(targets.Select(t => (t, 1)).ToArray())),
            // N ≥ 4: 2 per region (2N), no overflow, quorum N+1 with ≥1 per region.
            var n => new QuorumRequirement(2 * n, n + 1, Counts(targets.Select(t => (t, 2)).ToArray()), 0, n,
                Counts(targets.Select(t => (t, 1)).ToArray())),
        };
    }

    /// <summary>
    /// The small-cluster bootstrap model (single-node dev, a network still growing to its first regions):
    /// every writable node is a target, <see cref="GatewayOptions.WriteQuorumCount"/> acks are a quorum and
    /// <see cref="GatewayOptions.MinCopiesPerObject"/> is the copy floor. Regions are not enforced.
    /// </summary>
    public static QuorumRequirement Bootstrap(int writableNodes, int writeQuorum, int minCopies)
    {
        var none = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = Math.Max(Math.Max(writableNodes, minCopies), writeQuorum);
        return new QuorumRequirement(total, Math.Max(1, writeQuorum), none, total, 1, none)
        {
            RepairTarget = Math.Max(1, minCopies),
            EnforcesRegions = false,
        };
    }

    private static Dictionary<string, int> Counts(params (string Region, int Count)[] entries) =>
        entries.ToDictionary(e => e.Region, e => e.Count, StringComparer.OrdinalIgnoreCase);
}
