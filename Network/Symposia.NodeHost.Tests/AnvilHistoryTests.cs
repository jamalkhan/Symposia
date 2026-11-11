using Microsoft.Extensions.Logging.Abstractions;
using Symposia.NodeHost.Components;

namespace Symposia.NodeHost.Tests;

/// <summary>Anvil history pruning and the stale tmp sweep (issue #210).</summary>
public sealed class AnvilHistoryTests : IDisposable
{
    private readonly TempDir _dir = new();

    private string Snapshot(string name, DateTime createdUtc)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "state"), "{}");
        Directory.SetCreationTimeUtc(path, createdUtc);
        return path;
    }

    [Fact]
    public void AnvilArguments_PruneHistory()
    {
        var args = ChainComponent.AnvilArguments(8545, "/x/anvil.json").ToList();

        var i = args.IndexOf("--prune-history");
        Assert.True(i >= 0, "LocalDev anvil must prune history");
        Assert.Equal(ChainComponent.AnvilHistoryStates.ToString(), args[i + 1]);
        Assert.Contains("--state", args);
    }

    [Fact]
    public void FindStale_NoAnvilRunning_PicksEverySnapshot()
    {
        var old = Snapshot("anvil-state-01-11-2026-00-00aaaaaa", DateTime.UtcNow.AddDays(-3));
        var recent = Snapshot("anvil-state-11-11-2026-00-00bbbbbb", DateTime.UtcNow);
        Snapshot("unrelated", DateTime.UtcNow.AddDays(-3));

        Assert.Equal([old, recent], AnvilTmpSweeper.FindStale(_dir.Path, []).Order());
    }

    [Fact]
    public void FindStale_AnvilRunning_KeepsDirsItCouldOwn()
    {
        var started = DateTime.UtcNow.AddHours(-1);
        var stale = Snapshot("anvil-state-a", started.AddHours(-1));
        Snapshot("anvil-state-b", started.AddSeconds(-30));
        Snapshot("anvil-state-c", started.AddMinutes(10));

        Assert.Equal([stale], AnvilTmpSweeper.FindStale(_dir.Path, [started, started.AddMinutes(5)]));
    }

    [Fact]
    public void FindStale_UnreadableProcessList_PicksNothing()
    {
        Snapshot("anvil-state-a", DateTime.UtcNow.AddDays(-3));

        Assert.Empty(AnvilTmpSweeper.FindStale(_dir.Path, null));
    }

    [Fact]
    public void FindStale_MissingTmpDir_PicksNothing() =>
        Assert.Empty(AnvilTmpSweeper.FindStale(Path.Combine(_dir.Path, "nope"), []));

    [Fact]
    public void FindStale_SkipsSymlinks()
    {
        var target = Snapshot("elsewhere", DateTime.UtcNow.AddDays(-3));
        Directory.CreateSymbolicLink(Path.Combine(_dir.Path, "anvil-state-link"), target);

        Assert.Empty(AnvilTmpSweeper.FindStale(_dir.Path, []));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Delete_RemovesDirsAndToleratesMissingOnes()
    {
        var dir = Snapshot("anvil-state-a", DateTime.UtcNow);

        AnvilTmpSweeper.Delete([dir, Path.Combine(_dir.Path, "anvil-state-gone")], NullLogger.Instance);

        Assert.False(Directory.Exists(dir));
    }

    public void Dispose() => _dir.Dispose();
}
