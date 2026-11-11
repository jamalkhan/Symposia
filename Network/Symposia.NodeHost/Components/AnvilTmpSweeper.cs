using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Symposia.NodeHost.Components;

/// <summary>
/// Removes the history snapshot dirs (<c>anvil-state-*</c>) that anvil leaks into its tmp dir when it is
/// hard-killed (issue #210 FR4). Only dirs no running anvil can own are removed: those last written more
/// than <see cref="Grace"/> before both now and the earliest running anvil's start. If any running anvil's
/// start time can't be read, nothing is removed.
/// </summary>
internal static class AnvilTmpSweeper
{
    /// <summary>
    /// Floor on a dir's age. Covers anvils this host can't see as processes (a container sharing ~/.foundry,
    /// one starting mid-sweep) and timestamp skew; a live anvil keeps writing its dir, so it stays recent.
    /// </summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    private static int _sweeping;

    /// <summary>Anvil's default tmp dir; LocalDev never moves it with <c>--cache-path</c>.</summary>
    internal static string DefaultTmpDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "anvil", "tmp");

    /// <summary>
    /// Picks stale dirs now, then deletes them on a background task, at most one sweep per process (a quick
    /// restart must not race a sweep still deleting thousands of files). Call it before starting this
    /// host's anvil, so the dir that anvil creates can never be picked.
    /// </summary>
    internal static void SweepInBackground(string tmpDir, ILogger logger)
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
        {
            return;
        }

        var startTimes = RunningAnvilStartTimesUtc();
        if (startTimes is null)
        {
            logger.LogInformation("Skipping the stale anvil history sweep: a running anvil's start time could not be read.");
        }

        var stale = FindStale(tmpDir, startTimes, DateTime.UtcNow);
        if (stale.Count == 0)
        {
            Volatile.Write(ref _sweeping, 0);
            return;
        }

        logger.LogInformation("Removing {Count} stale anvil history dirs from {Dir}.", stale.Count, tmpDir);
        _ = Task.Run(() =>
        {
            try
            {
                Delete(stale, logger);
            }
            finally
            {
                Volatile.Write(ref _sweeping, 0);
            }
        }, CancellationToken.None);
    }

    /// <summary>The dirs safe to delete.</summary>
    /// <param name="anvilStartTimesUtc">Start times of running anvils; null when one couldn't be read.</param>
    internal static IReadOnlyList<string> FindStale(string tmpDir, IReadOnlyList<DateTime>? anvilStartTimesUtc, DateTime nowUtc)
    {
        if (anvilStartTimesUtc is null || !Directory.Exists(tmpDir))
        {
            return [];
        }

        var cutoff = (anvilStartTimesUtc.Count == 0 ? nowUtc : new[] { nowUtc, anvilStartTimesUtc.Min() }.Min()) - Grace;
        // Last-write time, not creation: it's portable (Linux has no settable birth time) and a live anvil keeps it recent.
        return new DirectoryInfo(tmpDir).EnumerateDirectories("anvil-state-*")
            .Where(dir => dir.LinkTarget is null && dir.LastWriteTimeUtc < cutoff)
            .Select(dir => dir.FullName)
            .ToList();
    }

    /// <summary>Deletes <paramref name="dirs"/>, logging (never throwing) on each failure; already-gone dirs are fine.</summary>
    internal static void Delete(IEnumerable<string> dirs, ILogger logger)
    {
        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not remove stale anvil history dir {Dir}: {Error}", dir, e.Message);
            }
        }
    }

    /// <summary>Start times of every running anvil, or null if any can't be read.</summary>
    internal static IReadOnlyList<DateTime>? RunningAnvilStartTimesUtc()
    {
        var processes = Process.GetProcessesByName("anvil");
        try
        {
            return processes.Select(p => p.StartTime.ToUniversalTime()).ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }
}
