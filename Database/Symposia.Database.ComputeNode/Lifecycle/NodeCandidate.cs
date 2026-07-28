namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// A compute node candidate for placement, as reported by #90's capacity declarations.
/// <see cref="SupportedPostgresMajorVersions"/> is the operator's declared version set (issue #102,
/// FR1.5/FR4.1) -- <c>null</c> means "declares support for every major" (back-compat default for
/// candidates registered before #102's version-aware routing).
/// </summary>
public sealed record NodeCandidate(
    string NodeId,
    string Region,
    int Tier,
    int AvailableCapacity,
    string Host,
    int Port,
    IReadOnlySet<int>? SupportedPostgresMajorVersions = null)
{
    public bool SupportsPostgresMajor(int major) =>
        SupportedPostgresMajorVersions is null || SupportedPostgresMajorVersions.Contains(major);
}
