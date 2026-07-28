using Symposia.Database.ComputeNode.Lifecycle;

namespace Symposia.Database.ComputeNode.Tests.Lifecycle;

/// <summary>Traces to #102's FR1 (Supported Version Window) and AC1.</summary>
public sealed class PostgresVersionCatalogTests
{
    [Fact]
    public void GetSupportedVersions_DefaultSeed_ReturnsThreeMajorsNewestFirst()
    {
        var catalog = new InMemoryPostgresVersionCatalog();

        var versions = catalog.GetSupportedVersions();

        Assert.Equal(3, versions.Count); // FR1.2's recommended default N=3
        Assert.Equal([17, 16, 15], versions.Select(v => v.Major));
    }

    [Fact]
    public void LatestSupportedMajor_ReturnsNewest()
    {
        var catalog = new InMemoryPostgresVersionCatalog();

        Assert.Equal(17, catalog.LatestSupportedMajor);
    }

    [Fact]
    public void OldestSupportedMajor_ReturnsOldest()
    {
        var catalog = new InMemoryPostgresVersionCatalog();

        Assert.Equal(15, catalog.OldestSupportedMajor);
    }

    [Fact]
    public void IsSupported_WithinWindow_True()
    {
        var catalog = new InMemoryPostgresVersionCatalog();

        Assert.True(catalog.IsSupported(16));
    }

    [Fact]
    public void IsSupported_OutsideWindow_False()
    {
        var catalog = new InMemoryPostgresVersionCatalog();

        Assert.False(catalog.IsSupported(13));
    }

    [Fact]
    public void EachSupportedVersion_HasPlatformEolDateNoLaterThanUpstreamPgdgEol()
    {
        // FR1.3: the platform EOL date must never be later than upstream PGDG's own EOL for that
        // major. Real upstream PGDG EOL dates (per the PostgreSQL Global Development Group's
        // published support policy) as of this spec: 15 -> 2027-11-11, 16 -> 2028-11-09, 17 -> 2029-11-08.
        var upstreamPgdgEol = new Dictionary<int, DateTimeOffset>
        {
            [15] = new(2027, 11, 11, 0, 0, 0, TimeSpan.Zero),
            [16] = new(2028, 11, 9, 0, 0, 0, TimeSpan.Zero),
            [17] = new(2029, 11, 8, 0, 0, 0, TimeSpan.Zero),
        };
        var catalog = new InMemoryPostgresVersionCatalog();

        foreach (var version in catalog.GetSupportedVersions())
            Assert.True(version.PlatformEolDate <= upstreamPgdgEol[version.Major]);
    }

    [Fact]
    public void ReplaceVersions_GovernanceReseed_ChangesWindowSize()
    {
        // Confirms N is config-driven, not hardcoded (mirrors QA TC-05's intent).
        var catalog = new InMemoryPostgresVersionCatalog();

        catalog.ReplaceVersions([new PostgresVersionInfo(17, DateTimeOffset.UtcNow.AddYears(3)), new PostgresVersionInfo(16, DateTimeOffset.UtcNow.AddYears(2))]);

        Assert.Equal(2, catalog.GetSupportedVersions().Count);
        Assert.False(catalog.IsSupported(15));
    }

    [Fact]
    public void Constructor_EmptySeed_Throws()
    {
        Assert.Throws<ArgumentException>(() => new InMemoryPostgresVersionCatalog([]));
    }
}
