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

public enum StorageMode
{
    Writable,

    /// <summary>Free disk (less the reserve) can't cover the unused allocation: reads only (#199).</summary>
    ReadOnly,

    /// <summary>Free disk is below the reserve: reads only, and the node reports unhealthy so its data is repaired elsewhere.</summary>
    Critical,
}

/// <summary>
/// Read-only mode (issue #199). With reserve R kept for the node's own files and writable free
/// W = F − R: read-only when W is under the unused allocation U, critical when F is under R.
/// Writes resume only once F ≥ 2.10 × U + R (at or past #197's warning-clear line) on
/// <see cref="ExitChecks"/> consecutive evaluations, so the node doesn't flap.
/// </summary>
public static class ReadOnlyMode
{
    public const long MinReserve = 256L << 20;
    public const long MaxReserve = 10L << 30;
    public const double ExitFactor = DiskLow.WarnFactor * DiskLow.RecoveryMargin;
    public const int ExitChecks = 2;

    /// <summary>max(1% of the volume, 256 MiB), capped at 10 GiB.</summary>
    public static long Reserve(long volumeTotal) => Math.Clamp(volumeTotal / 100, MinReserve, MaxReserve);

    /// <summary>Writable space the node can back: min(U, F − R), never negative.</summary>
    public static long Available(long free, long unused, long reserve) => Math.Max(0, Math.Min(unused, free - reserve));

    /// <param name="exitStreak">Consecutive evaluations (including this one) that met the exit line; updated here.</param>
    public static StorageMode Evaluate(StorageMode previous, long free, long unused, long reserve, ref int exitStreak)
    {
        if (free < reserve)
        {
            exitStreak = 0;
            return StorageMode.Critical;
        }

        if (free - reserve < unused)
        {
            exitStreak = 0;
            return StorageMode.ReadOnly;
        }

        if (previous == StorageMode.Writable)
        {
            exitStreak = 0;
            return StorageMode.Writable;
        }

        // Recovering from read-only (or critical): require the margin, held across checks.
        exitStreak = free >= ExitFactor * unused + reserve ? exitStreak + 1 : 0;
        return exitStreak >= ExitChecks ? StorageMode.Writable : StorageMode.ReadOnly;
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
    DateTimeOffset MeasuredAt,
    StorageMode Mode = StorageMode.Writable)
{
    /// <summary>Allocated but not yet used.</summary>
    public long Unused => Math.Max(0, Allocated - Used);

    public long WarnBelow => (long)(Storage.DiskLow.WarnFactor * Unused);

    public long ErrorBelow => (long)(Storage.DiskLow.ErrorFactor * Unused);

    /// <summary>Free disk kept for the node's own files (manifest, logs, chain state).</summary>
    public long Reserve => ReadOnlyMode.Reserve(VolumeTotal);

    /// <summary>Writable space the node reports (heartbeats, Probe, health): min(unused allocation, free − reserve).</summary>
    public long Available => ReadOnlyMode.Available(Free, Unused, Reserve);

    /// <summary>Used plus <see cref="Available"/>; under the allocation when the disk can't back it.</summary>
    public long Total => Used + Available;

    /// <summary>Free disk at which read-only ends.</summary>
    public long ExitAbove => (long)(ReadOnlyMode.ExitFactor * Unused) + Reserve;

    public bool ReadOnly => Mode != StorageMode.Writable;
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
    private StorageMode? _assertedMode;
    private int _exitStreak;
    private long _inflight;
    private long _writtenSinceMeasure;

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

    /// <summary>
    /// The latest disk measurement and mode, with used bytes read live from the in-memory counter so a
    /// write shows at once; taken on first read if the monitor hasn't ticked yet.
    /// </summary>
    public StorageCapacitySnapshot Current
    {
        get
        {
            var s = _current ?? Evaluate();
            var used = _manifest.UsedBytes;
            return used == s.Used ? s : s with { Used = used };
        }
    }

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
            var mode = ReadOnlyMode.Evaluate(previous?.Mode ?? StorageMode.Writable, free, unused, ReadOnlyMode.Reserve(total), ref _exitStreak);
            var snapshot = new StorageCapacitySnapshot(allocated, used, free, total, Path.GetFullPath(root), state, now, mode);
            Interlocked.Exchange(ref _writtenSinceMeasure, 0);
            _current = snapshot;
            if (previous is not null && previous.Mode != mode)
            {
                _logger.LogWarning("Storage mode {From} → {To}: free {Free}, unused allocation {Unused}, reserve {Reserve}",
                    previous.Mode, mode, Bytes.Human(free), Bytes.Human(unused), Bytes.Human(snapshot.Reserve));
            }

            AssertAlert(snapshot);
            AssertModeAlert(snapshot);
            return snapshot;
        }
    }

    /// <summary>Measures before the gRPC listener accepts writes (#199 FR5): hosted services start before Kestrel.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Evaluate();
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Admits <paramref name="bytes"/> of a write (#199): refused while read-only, or when it would
    /// exceed the space the node can back, counting writes in flight and those since the last
    /// measurement. A successful reservation must be released with <see cref="Release"/>.
    /// </summary>
    public bool TryReserve(long bytes)
    {
        var s = Current;
        if (s.ReadOnly)
        {
            return false;
        }

        var reserved = Interlocked.Add(ref _inflight, bytes);
        var budget = Math.Min(s.Allocated - _manifest.UsedBytes, s.Free - s.Reserve - Interlocked.Read(ref _writtenSinceMeasure));
        if (bytes > 0 && reserved > budget)
        {
            Interlocked.Add(ref _inflight, -bytes);
            return false;
        }

        return true;
    }

    /// <summary>Returns a reservation; <paramref name="stored"/> bytes landed on disk.</summary>
    public void Release(long reserved, long stored)
    {
        Interlocked.Add(ref _inflight, -reserved);
        Interlocked.Add(ref _writtenSinceMeasure, stored);
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
        _alerts.Clear(AlertKeys.StorageReadOnly);
    }

    private void AssertModeAlert(StorageCapacitySnapshot s)
    {
        if (_assertedMode == s.Mode && s.Mode == StorageMode.Writable)
        {
            return;
        }

        _assertedMode = s.Mode;
        if (s.Mode == StorageMode.Writable)
        {
            _alerts.Clear(AlertKeys.StorageReadOnly);
            return;
        }

        var figures = $"{Bytes.Human(s.Free)} free on {s.VolumeRoot}, {Bytes.Human(s.Unused)} of allocated storage unused, "
            + $"{Bytes.Human(s.Reserve)} kept in reserve.";
        if (s.Mode == StorageMode.Critical)
        {
            _alerts.Raise(AlertKeys.StorageReadOnly, AlertSeverity.Error, "Storage node almost out of disk",
                $"Free disk is below the reserve the node needs. It reports unhealthy, so the network is copying its data to other nodes; reads still work while they can. {figures}");
            return;
        }

        _alerts.Raise(AlertKeys.StorageReadOnly, AlertSeverity.Warning, "Storage node is read-only",
            $"The disk can't cover the space allocated to this node, so it serves the data it holds but takes no new data. {figures} "
            + $"Writes resume once free disk reaches {Bytes.Human(s.ExitAbove)}.");
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
