namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// A single supported Postgres major version entry (issue #102, FR1.1-FR1.3): the major number and
/// the platform's own published EOL date for it (governance-set, no later than upstream PGDG's EOL,
/// per FR1.3).
/// </summary>
public sealed record PostgresVersionInfo(int Major, DateTimeOffset PlatformEolDate);

/// <summary>
/// The governance-configured window of Postgres major versions the platform currently supports
/// (issue #102, FR1). Exposed publicly via <c>GET /platform/postgres-versions</c> and consumed
/// internally by provisioning (FR1.4/FR1.5) and the Compute Attachment Swap upgrade path
/// (FR2.1-FR2.5).
/// </summary>
public interface IPostgresVersionCatalog
{
    /// <summary>All currently supported majors, newest first, each with its platform EOL date.</summary>
    IReadOnlyList<PostgresVersionInfo> GetSupportedVersions();

    /// <summary>The current latest supported major (FR1.4's provisioning default).</summary>
    int LatestSupportedMajor { get; }

    /// <summary>The current oldest supported major (FR3.2's EOL force-upgrade target; not otherwise used by this pass).</summary>
    int OldestSupportedMajor { get; }

    bool IsSupported(int major);
}

/// <summary>
/// In-memory, config-driven version catalog (no EF/database layer exists in this codebase yet --
/// mirrors the <see cref="InMemoryComputeNodePlacementService"/> pattern of a <see cref="Lock"/>-guarded
/// in-memory collection). N (the rolling-window size) is implicit in whatever list governance seeds
/// at construction; the recommended default per the spec is the 3 most recent stable majors.
/// </summary>
public sealed class InMemoryPostgresVersionCatalog : IPostgresVersionCatalog
{
    /// <summary>
    /// Default seed: the platform's initial governance-ratified supported window (spec's recommended
    /// N=3), with platform EOL dates set at least 6 months ahead of and no later than the upstream
    /// PostgreSQL Global Development Group's own EOL dates for each major (FR1.3).
    /// </summary>
    public static IReadOnlyList<PostgresVersionInfo> DefaultVersions { get; } =
    [
        new(17, new DateTimeOffset(2029, 11, 8, 0, 0, 0, TimeSpan.Zero)),
        new(16, new DateTimeOffset(2028, 11, 9, 0, 0, 0, TimeSpan.Zero)),
        new(15, new DateTimeOffset(2027, 11, 11, 0, 0, 0, TimeSpan.Zero)),
    ];

    private readonly Lock _gate = new();
    private readonly List<PostgresVersionInfo> _versions;

    public InMemoryPostgresVersionCatalog(IEnumerable<PostgresVersionInfo>? seedVersions = null)
    {
        _versions = [.. (seedVersions ?? DefaultVersions).OrderByDescending(v => v.Major)];
        if (_versions.Count == 0)
            throw new ArgumentException("At least one supported Postgres major version is required.", nameof(seedVersions));
    }

    public IReadOnlyList<PostgresVersionInfo> GetSupportedVersions()
    {
        lock (_gate)
        {
            return [.. _versions];
        }
    }

    public int LatestSupportedMajor
    {
        get
        {
            lock (_gate)
            {
                return _versions[0].Major;
            }
        }
    }

    public int OldestSupportedMajor
    {
        get
        {
            lock (_gate)
            {
                return _versions[^1].Major;
            }
        }
    }

    public bool IsSupported(int major)
    {
        lock (_gate)
        {
            return _versions.Any(v => v.Major == major);
        }
    }

    /// <summary>Governance re-seed hook (e.g. ratifying a new N or updated EOL dates); not exercised by EOL enforcement in this pass.</summary>
    public void ReplaceVersions(IEnumerable<PostgresVersionInfo> versions)
    {
        lock (_gate)
        {
            var list = versions.OrderByDescending(v => v.Major).ToList();
            if (list.Count == 0)
                throw new ArgumentException("At least one supported Postgres major version is required.", nameof(versions));
            _versions.Clear();
            _versions.AddRange(list);
        }
    }
}
