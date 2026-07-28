namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>Issue #103, FR1.2: how broadly available an allowlisted extension is expected to be on compute nodes.</summary>
public enum ExtensionSupportTier
{
    /// <summary>Bundled and mandatory on every compute node image (FR2.3/AC6).</summary>
    Core,

    /// <summary>Widely available; expected on most nodes but not mandatory.</summary>
    Standard,

    /// <summary>Available only on a subset of nodes.</summary>
    Specialized,
}

/// <summary>
/// Issue #103, FR4.1: the privilege posture an extension requires to install and operate.
/// <see cref="Untrusted"/> extensions are never eligible for the allowlist at all (FR4.2/AC12) --
/// enforced as an invariant in <see cref="InMemoryExtensionAllowlist"/>, not merely documented here.
/// </summary>
public enum ExtensionPrivilegeClass
{
    /// <summary>No elevated OS/superuser privilege required to install or use (e.g. pgvector, postgis).</summary>
    Trusted,

    /// <summary>
    /// Requires superuser to install, but is safely usable by non-superuser tenant roles once
    /// installed (e.g. pg_cron). Per FR4.3, that superuser action happens once, by the operator,
    /// at node-image build time -- never per-tenant, per-database, or platform-triggered at runtime.
    /// </summary>
    RestrictedSuperuser,

    /// <summary>
    /// Provides a mechanism for arbitrary code execution or filesystem/network access outside
    /// Postgres's permission model. Structurally excluded from the allowlist (FR4.2) -- this value
    /// exists only so callers describing a rejected extension request have a name for the reason.
    /// </summary>
    Untrusted,
}

/// <summary>
/// A single Extension Allowlist entry (issue #103, FR1.1/FR1.2): extension name, supported version
/// range, compatible Postgres major version(s) (per #102's version policy), support tier, and
/// privilege class.
/// </summary>
public sealed record ExtensionAllowlistEntry(
    string Name,
    string MinVersion,
    string MaxVersion,
    IReadOnlySet<int> CompatiblePostgresMajors,
    ExtensionSupportTier SupportTier,
    ExtensionPrivilegeClass PrivilegeClass)
{
    /// <summary>
    /// FR5's structural verification simplification (no live Postgres to probe in this in-memory
    /// service layer): a declared version is considered valid when it parses and falls within
    /// [MinVersion, MaxVersion] inclusive.
    /// </summary>
    public bool IsVersionSupported(string declaredVersion)
    {
        if (!Version.TryParse(declaredVersion, out var declared)) return false;
        if (!Version.TryParse(MinVersion, out var min) || !Version.TryParse(MaxVersion, out var max)) return false;
        return declared >= min && declared <= max;
    }

    public bool IsCompatibleWithMajor(int postgresMajor) => CompatiblePostgresMajors.Contains(postgresMajor);
}

/// <summary>
/// The platform-defined Extension Allowlist (issue #103, FR1). In-memory, config-driven -- no
/// EF/database layer exists in this codebase yet, mirroring <see cref="InMemoryPostgresVersionCatalog"/>'s
/// pattern of a <see cref="Lock"/>-guarded in-memory collection. Exposed publicly via
/// <c>GET /platform/extensions</c>.
/// </summary>
public interface IExtensionAllowlist
{
    IReadOnlyList<ExtensionAllowlistEntry> GetEntries();

    ExtensionAllowlistEntry? Find(string extensionName);

    bool IsAllowlisted(string extensionName);
}

/// <summary>
/// In-memory allowlist seeded with the FR1.5 launch set. <see cref="Add"/> is the single mutation
/// path (governance re-seed / FR1.4 additions go through it) and bakes the FR4.2 invariant that no
/// <see cref="ExtensionPrivilegeClass.Untrusted"/> entry can ever be added -- not just a code comment,
/// an enforced throw, so the invariant survives even a future caller that doesn't read this comment.
/// </summary>
public sealed class InMemoryExtensionAllowlist : IExtensionAllowlist
{
    /// <summary>FR1.5's launch set: PostGIS/pgvector/pg_cron and pg_stat_statements (Core), uuid-ossp/pgcrypto.</summary>
    public static IReadOnlyList<ExtensionAllowlistEntry> DefaultEntries { get; } =
    [
        new("pg_stat_statements", "1.0", "1.11", new HashSet<int> { 15, 16, 17 }, ExtensionSupportTier.Core, ExtensionPrivilegeClass.Trusted),
        new("pgvector", "0.5.0", "0.8.0", new HashSet<int> { 15, 16, 17 }, ExtensionSupportTier.Standard, ExtensionPrivilegeClass.Trusted),
        new("postgis", "3.3.0", "3.5.0", new HashSet<int> { 15, 16, 17 }, ExtensionSupportTier.Standard, ExtensionPrivilegeClass.Trusted),
        new("pg_cron", "1.5.0", "1.6.4", new HashSet<int> { 15, 16, 17 }, ExtensionSupportTier.Standard, ExtensionPrivilegeClass.RestrictedSuperuser),
        new("uuid-ossp", "1.1", "1.1", new HashSet<int> { 15, 16, 17 }, ExtensionSupportTier.Standard, ExtensionPrivilegeClass.Trusted),
        new("pgcrypto", "1.3", "1.3", new HashSet<int> { 15, 16, 17 }, ExtensionSupportTier.Standard, ExtensionPrivilegeClass.Trusted),
    ];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ExtensionAllowlistEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryExtensionAllowlist(IEnumerable<ExtensionAllowlistEntry>? seedEntries = null)
    {
        foreach (var entry in seedEntries ?? DefaultEntries)
            Add(entry);
    }

    /// <summary>
    /// FR4.2/AC12 invariant: throws rather than accepting an Untrusted-class entry, at both seed time
    /// and any later governance re-seed (FR1.4) -- there is no code path in this class that can
    /// smuggle an Untrusted extension onto the allowlist.
    /// </summary>
    public void Add(ExtensionAllowlistEntry entry)
    {
        if (entry.PrivilegeClass == ExtensionPrivilegeClass.Untrusted)
        {
            throw new InvalidOperationException(
                $"Extension '{entry.Name}' is Untrusted-class and can never be added to the allowlist (issue #103 FR4.2/AC12).");
        }

        lock (_gate)
        {
            _entries[entry.Name] = entry;
        }
    }

    public IReadOnlyList<ExtensionAllowlistEntry> GetEntries()
    {
        lock (_gate)
        {
            return [.. _entries.Values];
        }
    }

    public ExtensionAllowlistEntry? Find(string extensionName)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(extensionName);
        }
    }

    public bool IsAllowlisted(string extensionName)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(extensionName);
        }
    }
}
