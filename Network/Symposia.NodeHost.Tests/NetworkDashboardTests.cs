using Symposia.Network.Node;
using Symposia.NodeHost.Diagnostics;

namespace Symposia.NodeHost.Tests;

/// <summary>Network Dashboard, Symposia Storage Network section (issue #198).</summary>
public sealed class NetworkDashboardTests
{
    private const long GB = 1L << 30;

    private static readonly NodeHealthReport Self = new("0xself",
        new StorageHealth(false, HealthLevel.Off, null, null, null), new ComputeHealth(false, HealthLevel.Off, null, null));

    private static NetworkNodeRow Storage(string node, long used, long free, bool readOnly = false, HealthLevel level = HealthLevel.Ok, int protocol = 2) =>
        new(node, $"http://{node}:5180", node, false, true,
            new StorageHealth(true, level, used + free, free, null) { UsedBytes = used, ReadOnly = readOnly, Mode = readOnly ? "ReadOnly" : "Writable" },
            new ComputeHealth(false, HealthLevel.Off, null, null))
        {
            Services = protocol == 0 ? null : new NodeServices(node, new NodeServices.StorageService($"http://{node}:6180"), protocol),
        };

    private static NetworkNodeRow Unreachable(string node) =>
        new(node, $"http://{node}:5180", node, false, true,
            new StorageHealth(false, HealthLevel.Unknown, null, null, "Unreachable"),
            new ComputeHealth(false, HealthLevel.Unknown, null, "Unreachable"));

    private static NetworkCrawl Crawl(params NetworkNodeRow[] rows) => new(Self, rows, ProtocolSchedule.None, null, DateTimeOffset.UtcNow);

    // Spec scenario: 4 nodes using 1,2,3,4 GB reporting 10,10,10,0 GB available; node 4 read-only.
    [Fact]
    public void Aggregates_Totals_AndPopulation()
    {
        var crawl = Crawl(Storage("a", 1 * GB, 10 * GB), Storage("b", 2 * GB, 10 * GB), Storage("c", 3 * GB, 10 * GB),
            Storage("d", 4 * GB, 0, readOnly: true, level: HealthLevel.Warn));

        var s = NetworkDashboard.Summarize(crawl, null);

        Assert.Equal(10 * GB, s.UsedBytes);
        Assert.Equal(30 * GB, s.AvailableBytes);
        Assert.Equal(new StoragePopulation(4, 3, 1, 0), s.Population);
        Assert.Null(s.MeanUptimePercent);
    }

    [Fact]
    public void Precedence_UnreachableThenUnhealthyThenReadOnly_EachCountedOnce()
    {
        var crawl = Crawl(Unreachable("u"), Storage("bad", GB, 0, readOnly: true, level: HealthLevel.Bad), Storage("ro", GB, 0, readOnly: true), Storage("ok", GB, GB));

        var s = NetworkDashboard.Summarize(crawl, null);

        Assert.Equal(new StoragePopulation(4, 1, 1, 2), s.Population);
        Assert.Equal(1, s.UsedUnknown);
    }

    [Fact]
    public void UnreachableNodes_FallBackToTheirLastCheckpoint()
    {
        var past = new StorageHistory(
            new Dictionary<string, double> { ["a"] = 9900, ["u"] = 9500 },
            new Dictionary<string, (ulong, ulong)> { ["u"] = (5UL * GB, 7UL * GB) },
            30, DateTimeOffset.UtcNow);

        var s = NetworkDashboard.Summarize(Crawl(Storage("a", GB, GB), Unreachable("u"), Storage("c", GB, GB)), past);

        Assert.Equal(7 * GB, s.UsedBytes);
        Assert.Equal(9 * GB, s.AvailableBytes);
        Assert.Equal(1, s.FromCheckpoint);
        Assert.Equal(0, s.UsedUnknown);
        Assert.Equal(97.0, s.MeanUptimePercent);
        Assert.Equal(2, s.UptimeNodes);
        Assert.Equal(1, s.UptimeNodesWithoutData);
    }

    [Fact]
    public void Versions_TopFive_MostPopularFirst_WithShares()
    {
        var rows = Enumerable.Range(0, 6).Select(i => Storage($"v2-{i}", GB, GB, protocol: 2))
            .Concat(Enumerable.Range(0, 3).Select(i => Storage($"v1-{i}", GB, GB, protocol: 1)))
            .Append(Storage("old", GB, GB, protocol: 0))
            .ToArray();

        var versions = NetworkDashboard.Summarize(Crawl(rows), null).Versions;

        Assert.Equal(
            [new VersionShare("protocol v2", 6, 60.0), new VersionShare("protocol v1", 3, 30.0), new VersionShare("unknown", 1, 10.0)],
            versions);
    }

    [Fact]
    public void Versions_AreCappedAtFive_TiesBrokenByNewerVersion()
    {
        var rows = Enumerable.Range(1, 7).Select(v => Storage($"n{v}", GB, GB, protocol: v)).ToArray();

        var versions = NetworkDashboard.Summarize(Crawl(rows), null).Versions;

        Assert.Equal(5, versions.Count);
        Assert.Equal("protocol v7", versions[0].Version);
        Assert.Equal("protocol v3", versions[^1].Version);
    }

    [Fact]
    public void ComputeOnlyAndUnregisteredNodes_AreNotStorageNodes()
    {
        var compute = new NetworkNodeRow("c", null, null, false, true,
            new StorageHealth(false, HealthLevel.Off, null, null, null), new ComputeHealth(true, HealthLevel.Ok, 5, null));
        var unregistered = Storage("x", GB, GB) with { Registered = false };

        Assert.Equal(1, NetworkDashboard.Summarize(Crawl(compute, unregistered, Storage("s", GB, GB)), null).Population.Registered);
    }

    [Theory]
    [InlineData(0, 9, 30, 10)]
    [InlineData(0, 719, 30, 30)]
    [InlineData(5, 4, 30, 0)]
    public void SampleEpochs_IsBounded_AndIncludesTheLatest(long first, long last, int max, int expectedCount)
    {
        var epochs = ChainStorageHistorySource.SampleEpochs(first, last, max);

        Assert.Equal(expectedCount, epochs.Count);
        if (expectedCount > 0)
        {
            Assert.Equal(last, epochs[^1]);
            Assert.Equal(first, epochs[0]);
        }
    }
}
