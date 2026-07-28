namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// A compute node candidate for placement, as reported by #90's capacity declarations.
/// <see cref="SupportedPostgresMajorVersions"/> is the operator's declared version set (issue #102,
/// FR1.5/FR4.1) -- <c>null</c> means "declares support for every major" (back-compat default for
/// candidates registered before #102's version-aware routing).
/// <see cref="DeclaredExtensionVersions"/> is the operator's declared-and-verified extension set
/// (issue #103, FR2.1/FR2.5), keyed by extension name (case-insensitive) with the installed version
/// as the value; <c>null</c>/empty means "declares no extensions" (distinct from #102's major-version
/// null-means-all default, since extensions have no notion of a universal default).
/// </summary>
public sealed record NodeCandidate(
    string NodeId,
    string Region,
    int Tier,
    int AvailableCapacity,
    string Host,
    int Port,
    IReadOnlySet<int>? SupportedPostgresMajorVersions = null,
    IReadOnlyDictionary<string, string>? DeclaredExtensionVersions = null)
{
    public bool SupportsPostgresMajor(int major) =>
        SupportedPostgresMajorVersions is null || SupportedPostgresMajorVersions.Contains(major);

    /// <summary>FR3.2/AC7: true when this node's declared extension set is a superset of <paramref name="required"/>.</summary>
    public bool DeclaresExtensions(IReadOnlyCollection<string> required)
    {
        if (required.Count == 0) return true;
        if (DeclaredExtensionVersions is null) return false;
        return required.All(name => DeclaredExtensionVersions.Keys.Contains(name, StringComparer.OrdinalIgnoreCase));
    }
}
