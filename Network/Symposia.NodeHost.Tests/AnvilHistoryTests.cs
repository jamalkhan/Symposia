using Microsoft.Extensions.Logging.Abstractions;
using Symposia.Blockchain.Gateway.Chain;
using Symposia.NodeHost.Components;

namespace Symposia.NodeHost.Tests;

/// <summary>Anvil history pruning and the stale tmp sweep (issue #210).</summary>
public sealed class AnvilHistoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempDir _dir = new();

    private string Snapshot(string name, DateTime writtenUtc)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "state"), "{}");
        Directory.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void AnvilArguments_PruneHistoryWithTheSharedBound()
    {
        var args = ChainComponent.AnvilArguments(8545, "/x/anvil.json").ToList();

        var i = args.IndexOf("--prune-history");
        Assert.True(i >= 0, "LocalDev anvil must prune history");
        Assert.Equal(AnvilArgs.HistoryStates.ToString(), args[i + 1]);
        Assert.Contains("--state", args);
        Assert.Equal(["--port", "0", .. AnvilArgs.PruneHistory], AnvilArgs.Fixture());
    }

    [Fact]
    public void FindStale_NoAnvilRunning_PicksAllButFreshDirs()
    {
        var old = Snapshot("anvil-state-01-11-2026-00-00aaaaaa", Now.AddDays(-3));
        var older = Snapshot("anvil-state-01-11-2026-00-00bbbbbb", Now.AddDays(-9));
        Snapshot("anvil-state-11-11-2026-00-00cccccc", Now.AddSeconds(-30)); // an anvil that started mid-sweep
        Snapshot("unrelated", Now.AddDays(-3));

        Assert.Equal([old, older], AnvilTmpSweeper.FindStale(_dir.Path, [], Now).Order());
    }

    [Fact]
    public void FindStale_AnvilRunning_KeepsDirsItCouldOwn()
    {
        var started = Now.AddHours(-1);
        var stale = Snapshot("anvil-state-a", started.AddHours(-1));
        Snapshot("anvil-state-b", started.AddSeconds(-30));
        Snapshot("anvil-state-c", started.AddMinutes(10));

        Assert.Equal([stale], AnvilTmpSweeper.FindStale(_dir.Path, [started, started.AddMinutes(5)], Now));
    }

    [Fact]
    public void FindStale_UnreadableProcessList_PicksNothing()
    {
        Snapshot("anvil-state-a", Now.AddDays(-3));

        Assert.Empty(AnvilTmpSweeper.FindStale(_dir.Path, null, Now));
    }

    [Fact]
    public void FindStale_MissingTmpDir_PicksNothing() =>
        Assert.Empty(AnvilTmpSweeper.FindStale(Path.Combine(_dir.Path, "nope"), [], Now));

    [Fact]
    public void FindStale_SkipsSymlinks()
    {
        var target = Snapshot("elsewhere", Now.AddDays(-3));
        Directory.CreateSymbolicLink(Path.Combine(_dir.Path, "anvil-state-link"), target);

        Assert.Empty(AnvilTmpSweeper.FindStale(_dir.Path, [], Now));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Delete_RemovesDirsAndToleratesMissingOnes()
    {
        var dir = Snapshot("anvil-state-a", Now);

        AnvilTmpSweeper.Delete([dir, Path.Combine(_dir.Path, "anvil-state-gone")], NullLogger.Instance);

        Assert.False(Directory.Exists(dir));
    }

    public void Dispose() => _dir.Dispose();
}
