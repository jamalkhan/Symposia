using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Symposia.NodeHost.Components;

/// <summary>
/// Removes the history snapshot dirs (<c>anvil-state-*</c>) that anvil leaks into its tmp dir when it is
/// hard-killed (issue #210 FR4). Only dirs no running anvil can own are removed: if no anvil runs, all of
/// them; otherwise only those created well before the earliest running anvil started. If any running
/// anvil's start time can't be read, nothing is removed.
/// </summary>
internal static class AnvilTmpSweeper
{
    /// <summary>Slack for clock and filesystem timestamp skew between an anvil starting and creating its dir.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    /// <summary>Anvil's default tmp dir; LocalDev never moves it with <c>--cache-path</c>.</summary>
    internal static string DefaultTmpDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "anvil", "tmp");

    /// <summary>
    /// The dirs safe to delete right now. Call it before starting this host's anvil, so the dir that anvil
    /// creates can never be picked.
    /// </summary>
    /// <param name="anvilStartTimesUtc">Start times of running anvils; null when one couldn't be read.</param>
    internal static IReadOnlyList<string> FindStale(string tmpDir, IReadOnlyList<DateTime>? anvilStartTimesUtc)
    {
        if (anvilStartTimesUtc is null || !Directory.Exists(tmpDir))
        {
            return [];
        }

        DateTime? cutoff = anvilStartTimesUtc.Count == 0 ? null : anvilStartTimesUtc.Min() - Grace;
        return new DirectoryInfo(tmpDir).EnumerateDirectories("anvil-state-*")
            .Where(dir => dir.LinkTarget is null && (cutoff is null || dir.CreationTimeUtc < cutoff))
            .Select(dir => dir.FullName)
            .ToList();
    }

    /// <summary>Deletes <paramref name="dirs"/>, logging (never throwing) on each failure.</summary>
    internal static void Delete(IEnumerable<string> dirs, ILogger logger)
    {
        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
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
