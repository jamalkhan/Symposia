using System.Globalization;
using Microsoft.Extensions.Options;
using Symposia.Network.Node.Alerts;

namespace Symposia.BlobStorage.StorageNode.Storage;

/// <summary>Free and total bytes of the volume holding a path.</summary>
public interface IDiskSpace
{
    (long Free, long Total) Measure(string path);
}

/// <summary>
/// Measures the volume the storage root actually lives on: a symlinked root is followed to its
/// target, and <see cref="DriveInfo"/> on Unix resolves a path to the mount that contains it.
/// </summary>
public sealed class DriveDiskSpace : IDiskSpace
{
    public (long Free, long Total) Measure(string path)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(path));
        var resolved = dir.LinkTarget is null ? dir.FullName : dir.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir.FullName;
        var drive = new DriveInfo(resolved);
        return (drive.AvailableFreeSpace, drive.TotalSize);
    }
}

public enum DiskLowState
{
    Ok,
    Warning,
    Error,
}

/// <summary>
/// Low disk against allocation (issue #197): Error when free disk is under the unused allocation,
/// Warning under twice it. Getting worse applies at once; recovering needs 5% clearance past the
/// line (1.05× to leave Error, 2.10× to clear Warning) so a disk hovering at a line doesn't flap.
/// A fully used allocation (unused = 0) is never low.
/// </summary>
public static class DiskLow
{
    public const double ErrorFactor = 1.0;
    public const double WarnFactor = 2.0;
    public const double RecoveryMargin = 1.05;

    public static DiskLowState Evaluate(DiskLowState previous, long free, long unused)
    {
        if (unused <= 0)
        {
            return DiskLowState.Ok;
        }

        var raw = free < ErrorFactor * unused ? DiskLowState.Error
            : free < WarnFactor * unused ? DiskLowState.Warning
            : DiskLowState.Ok;
        if (raw >= previous)
        {
            return raw;
        }

        // Recovering: each step down needs the margin past its line.
        if (previous == DiskLowState.Error && free < ErrorFactor * RecoveryMargin * unused)
        {
            return DiskLowState.Error;
        }

        if (raw == DiskLowState.Ok && free < WarnFactor * RecoveryMargin * unused)
        {
            return DiskLowState.Warning;
        }

        return raw;
    }
}

/// <summary>One measurement of the storage node's allocation against its disk.</summary>
public sealed record StorageCapacitySnapshot(
    long Allocated,
    long Used,
    long Free,
    long VolumeTotal,
    string VolumeRoot,
    DiskLowState DiskLow,
    DateTimeOffset MeasuredAt)
{
    /// <summary>Allocated but not yet used.</summary>
    public long Unused => Math.Max(0, Allocated - Used);

    public long WarnBelow => (long)(Storage.DiskLow.WarnFactor * Unused);

    public long ErrorBelow => (long)(Storage.DiskLow.ErrorFactor * Unused);
}

/// <summary>
/// Measures the storage node's capacity every 30 s (issues #197, #199) and publishes an immutable
/// <see cref="Current"/> snapshot, so Probe, heartbeats and health never do disk or SQL work
/// themselves. Raises and clears the <see cref="AlertKeys.StorageDiskLow"/> Operator Alert.
/// </summary>
public sealed class StorageCapacityMonitor : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ReconcileEvery = TimeSpan.FromMinutes(5);

    private readonly ManifestStore _manifest;
    private readonly IOptions<StorageNodeOptions> _options;
    private readonly IDiskSpace _disk;
    private readonly IOperatorAlertSink _alerts;
    private readonly ILogger<StorageCapacityMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DateTimeOffset _lastReconcile;
    private volatile StorageCapacitySnapshot? _current;
    private DiskLowState? _asserted;

    public StorageCapacityMonitor(
        ManifestStore manifest,
        IOptions<StorageNodeOptions> options,
        IDiskSpace disk,
        IOperatorAlertSink alerts,
        ILogger<StorageCapacityMonitor> logger,
        TimeProvider? time = null)
    {
        _manifest = manifest;
        _options = options;
        _disk = disk;
        _alerts = alerts;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _lastReconcile = _time.GetUtcNow();
    }

    /// <summary>The latest measurement; taken on first read if the monitor hasn't ticked yet.</summary>
    public StorageCapacitySnapshot Current => _current ?? Evaluate();

    /// <summary>Measures now, publishes the snapshot, and raises or clears the disk-low alert.</summary>
    public StorageCapacitySnapshot Evaluate()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _lastReconcile >= ReconcileEvery)
            {
                _lastReconcile = now;
                var drift = _manifest.ReconcileUsedBytes();
                if (drift != 0)
                {
                    _logger.LogInformation("Used-bytes counter corrected by {Drift} bytes", drift);
                }
            }

            var root = _options.Value.StorageRoot;
            var previous = _current;
            long free, total;
            try
            {
                (free, total) = _disk.Measure(root);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // Keep the last reading (and its state) rather than guessing.
                _logger.LogWarning("Could not read free space for {Root}: {Message}", root, ex.Message);
                if (previous is not null)
                {
                    return previous;
                }

                (free, total) = (0, 0);
            }

            var allocated = Math.Max(0, _options.Value.MaxCapacityBytes);
            var used = _manifest.UsedBytes;
            var unused = Math.Max(0, allocated - used);
            var state = DiskLow.Evaluate(previous?.DiskLow ?? DiskLowState.Ok, free, unused);
            var snapshot = new StorageCapacitySnapshot(allocated, used, free, total, Path.GetFullPath(root), state, now);
            _current = snapshot;
            AssertAlert(snapshot);
            return snapshot;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        do
        {
            try
            {
                Evaluate();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Storage capacity check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // A stopped role is reported by its component status; its disk alerts no longer apply.
        _alerts.Clear(AlertKeys.StorageDiskLow);
    }

    private void AssertAlert(StorageCapacitySnapshot s)
    {
        // Re-asserted on the first evaluation after start so the host keeps restored alerts (#196).
        if (_asserted == s.DiskLow && s.DiskLow == DiskLowState.Ok)
        {
            return;
        }

        _asserted = s.DiskLow;
        if (s.DiskLow == DiskLowState.Ok)
        {
            _alerts.Clear(AlertKeys.StorageDiskLow);
            return;
        }

        var detail = $"{Bytes.Human(s.Free)} free on {s.VolumeRoot}, {Bytes.Human(s.Unused)} of allocated storage still unused. "
            + $"Warning below {Bytes.Human(s.WarnBelow)}, error below {Bytes.Human(s.ErrorBelow)}.";
        if (s.DiskLow == DiskLowState.Error)
        {
            detail += " The storage node is, or is about to be, read-only: free up disk space or lower its allocation.";
        }

        _alerts.Raise(AlertKeys.StorageDiskLow, s.DiskLow == DiskLowState.Error ? AlertSeverity.Error : AlertSeverity.Warning,
            "Low disk space", detail);
    }
}

public static class Bytes
{
    /// <summary>Binary units, one decimal: 1536 MiB → "1.5 GB".</summary>
    public static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}
