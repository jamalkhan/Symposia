using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Symposia.BlobStorage.StorageNode.Storage;
using Symposia.NodeHost.Components;

namespace Symposia.NodeHost.Diagnostics;

/// <summary>Indicator colour for one role on the Network tab.</summary>
public enum HealthLevel
{
    /// <summary>Role not configured on that node (grey).</summary>
    Off,
    Ok,
    Warn,
    Bad,
    /// <summary>Node could not be reached or reported nothing (grey, with a reason).</summary>
    Unknown,
}

public sealed record StorageHealth(bool Configured, HealthLevel Level, long? CapacityBytes, long? FreeBytes, string? Detail);

public sealed record ComputeHealth(bool Configured, HealthLevel Level, double? CpuPercent, string? Detail);

/// <summary>What one node reports about itself at <see cref="NodeHealth.Path"/>.</summary>
public sealed record NodeHealthReport(string Node, StorageHealth Storage, ComputeHealth Compute);

/// <summary>
/// This host's storage and compute health, served publicly by the role apps so
/// other nodes can colour their Network tab. Thresholds: storage is yellow under
/// 40% free and red at 20% free or less; compute is yellow over 20% CPU and red over 50%.
/// </summary>
public sealed class NodeHealth(ComponentSupervisor supervisor, string nodeAddress)
{
    public const string Path = "/v1/node/health";

    public const double StorageWarnFreeFraction = 0.40;
    public const double StorageBadFreeFraction = 0.20;
    public const double ComputeWarnCpuPercent = 20;
    public const double ComputeBadCpuPercent = 50;

    public NodeHealthReport Local() => new(nodeAddress, Storage(), Compute());

    private StorageHealth Storage()
    {
        var settings = supervisor.Context.Settings.Storage;
        if (!settings.Enabled)
        {
            return new StorageHealth(false, HealthLevel.Off, null, null, null);
        }

        var component = supervisor.Storage;
        var manifest = component.Services?.GetService<ManifestStore>();
        if (component.Status != ComponentStatus.Running || manifest is null)
        {
            return new StorageHealth(true, HealthLevel.Bad, settings.MaxCapacityBytes, null, $"Storage is {component.Status}");
        }

        // Free is the smaller of the unused quota and what the disk actually has left.
        var capacity = settings.MaxCapacityBytes;
        var free = Math.Max(0, capacity - manifest.SumSizeBytes());
        try
        {
            free = Math.Min(free, new DriveInfo(supervisor.Context.RoleDataDir("storage")).AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Quota-only figure is still meaningful.
        }

        return new StorageHealth(true, StorageLevel(free, capacity), capacity, free, null);
    }

    private ComputeHealth Compute()
    {
        var settings = supervisor.Context.Settings.Compute;
        if (!settings.Enabled)
        {
            return new ComputeHealth(false, HealthLevel.Off, null, null);
        }

        var component = supervisor.Compute;
        if (component.Status != ComponentStatus.Running)
        {
            return new ComputeHealth(true, HealthLevel.Bad, null, $"Compute is {component.Status}");
        }

        var cpu = CpuPercent();
        return cpu is null
            ? new ComputeHealth(true, HealthLevel.Ok, null, "CPU usage not available on this platform")
            : new ComputeHealth(true, ComputeLevel(cpu.Value), cpu, null);
    }

    public static HealthLevel StorageLevel(long freeBytes, long capacityBytes)
    {
        if (capacityBytes <= 0)
        {
            return HealthLevel.Bad;
        }

        var fraction = (double)freeBytes / capacityBytes;
        return fraction <= StorageBadFreeFraction ? HealthLevel.Bad
            : fraction < StorageWarnFreeFraction ? HealthLevel.Warn
            : HealthLevel.Ok;
    }

    public static HealthLevel ComputeLevel(double cpuPercent) =>
        cpuPercent > ComputeBadCpuPercent ? HealthLevel.Bad
        : cpuPercent > ComputeWarnCpuPercent ? HealthLevel.Warn
        : HealthLevel.Ok;

    /// <summary>
    /// Machine-wide CPU use, approximated as the 1-minute load average over the core
    /// count (capped at 100). Null where load average isn't available (Windows).
    /// </summary>
    private static double? CpuPercent()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var loads = new double[3];
        if (getloadavg(loads, 3) < 1)
        {
            return null;
        }

        return Math.Round(Math.Min(100, loads[0] / Environment.ProcessorCount * 100), 1);
    }

    [DllImport("libc", SetLastError = false)]
    private static extern int getloadavg(double[] loadavg, int nelem);
}
