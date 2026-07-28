using Symposia.Database.ComputeNode.Lifecycle;

namespace Symposia.Database.ComputeNode.Tests.Lifecycle;

/// <summary>Traces to #103's FR1 (Platform-Defined Extension Allowlist) and AC1, AC4, AC12.</summary>
public sealed class ExtensionAllowlistTests
{
    [Fact]
    public void GetEntries_DefaultSeed_EveryEntryHasAllFields()
    {
        var allowlist = new InMemoryExtensionAllowlist();

        var entries = allowlist.GetEntries();

        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.MinVersion));
            Assert.False(string.IsNullOrWhiteSpace(entry.MaxVersion));
            Assert.NotEmpty(entry.CompatiblePostgresMajors);
        }
    }

    [Fact]
    public void DefaultSeed_IncludesLaunchSetAtStandardOrAbove()
    {
        // FR1.5/AC4: PostGIS, pgvector, pg_cron present at Standard tier or above.
        var allowlist = new InMemoryExtensionAllowlist();

        Assert.True(allowlist.Find("postgis")!.SupportTier >= ExtensionSupportTier.Standard);
        Assert.True(allowlist.Find("pgvector")!.SupportTier >= ExtensionSupportTier.Standard);
        Assert.True(allowlist.Find("pg_cron")!.SupportTier >= ExtensionSupportTier.Standard);
    }

    [Fact]
    public void DefaultSeed_PgStatStatements_IsCore()
    {
        var allowlist = new InMemoryExtensionAllowlist();

        Assert.Equal(ExtensionSupportTier.Core, allowlist.Find("pg_stat_statements")!.SupportTier);
    }

    [Fact]
    public void DefaultSeed_PgCron_IsRestrictedSuperuser()
    {
        // FR4.1: pg_cron requires superuser to install but is safely usable once installed.
        var allowlist = new InMemoryExtensionAllowlist();

        Assert.Equal(ExtensionPrivilegeClass.RestrictedSuperuser, allowlist.Find("pg_cron")!.PrivilegeClass);
    }

    [Theory]
    [InlineData("pgvector")]
    [InlineData("postgis")]
    [InlineData("uuid-ossp")]
    [InlineData("pgcrypto")]
    public void DefaultSeed_TrustedExtensions_AreTrustedClass(string name)
    {
        var allowlist = new InMemoryExtensionAllowlist();

        Assert.Equal(ExtensionPrivilegeClass.Trusted, allowlist.Find(name)!.PrivilegeClass);
    }

    [Fact]
    public void IsAllowlisted_UnknownExtension_False()
    {
        var allowlist = new InMemoryExtensionAllowlist();

        Assert.False(allowlist.IsAllowlisted("dblink"));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        var allowlist = new InMemoryExtensionAllowlist();

        Assert.NotNull(allowlist.Find("PgVector"));
    }

    [Fact]
    public void Add_UntrustedClassEntry_Throws()
    {
        // FR4.2/AC12: baked-in invariant, not just a code comment -- adding an Untrusted extension
        // must be structurally impossible, even via the governance re-seed path.
        var allowlist = new InMemoryExtensionAllowlist();
        var untrusted = new ExtensionAllowlistEntry("dblink", "1.2", "1.2", new HashSet<int> { 17 }, ExtensionSupportTier.Specialized, ExtensionPrivilegeClass.Untrusted);

        Assert.Throws<InvalidOperationException>(() => allowlist.Add(untrusted));
        Assert.False(allowlist.IsAllowlisted("dblink"));
    }

    [Fact]
    public void Constructor_SeedContainingUntrusted_Throws()
    {
        var untrusted = new ExtensionAllowlistEntry("dblink", "1.2", "1.2", new HashSet<int> { 17 }, ExtensionSupportTier.Specialized, ExtensionPrivilegeClass.Untrusted);

        Assert.Throws<InvalidOperationException>(() => new InMemoryExtensionAllowlist([untrusted]));
    }

    [Fact]
    public void IsVersionSupported_WithinRange_True()
    {
        var entry = new ExtensionAllowlistEntry("pgvector", "0.5.0", "0.8.0", new HashSet<int> { 17 }, ExtensionSupportTier.Standard, ExtensionPrivilegeClass.Trusted);

        Assert.True(entry.IsVersionSupported("0.7.0"));
    }

    [Fact]
    public void IsVersionSupported_OutsideRange_False()
    {
        var entry = new ExtensionAllowlistEntry("pgvector", "0.5.0", "0.8.0", new HashSet<int> { 17 }, ExtensionSupportTier.Standard, ExtensionPrivilegeClass.Trusted);

        Assert.False(entry.IsVersionSupported("99.0.0"));
    }

    [Fact]
    public void IsVersionSupported_Unparseable_False()
    {
        var entry = new ExtensionAllowlistEntry("pgvector", "0.5.0", "0.8.0", new HashSet<int> { 17 }, ExtensionSupportTier.Standard, ExtensionPrivilegeClass.Trusted);

        Assert.False(entry.IsVersionSupported("not-a-version"));
    }

    [Fact]
    public void IsCompatibleWithMajor_OutsideCompatibleSet_False()
    {
        var entry = new ExtensionAllowlistEntry("pgvector", "0.5.0", "0.8.0", new HashSet<int> { 17 }, ExtensionSupportTier.Standard, ExtensionPrivilegeClass.Trusted);

        Assert.False(entry.IsCompatibleWithMajor(15));
    }

    [Fact]
    public void Add_ReplacesExistingEntryOfSameName()
    {
        var allowlist = new InMemoryExtensionAllowlist();
        var replacement = new ExtensionAllowlistEntry("pgvector", "0.8.0", "0.9.0", new HashSet<int> { 17 }, ExtensionSupportTier.Specialized, ExtensionPrivilegeClass.Trusted);

        allowlist.Add(replacement);

        Assert.Equal(ExtensionSupportTier.Specialized, allowlist.Find("pgvector")!.SupportTier);
    }
}
