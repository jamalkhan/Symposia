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

public sealed record StorageHealth(bool Configured, HealthLevel Level, long? CapacityBytes, long? FreeBytes, string? Detail)
{
    /// <summary>Bytes stored (#198); null from nodes predating #199.</summary>
    public long? UsedBytes { get; init; }

    /// <summary>Refusing new writes for disk space (#199); null from older nodes.</summary>
    public bool? ReadOnly { get; init; }

    /// <summary>Writable, ReadOnly or Critical (#199); null from older nodes.</summary>
    public string? Mode { get; init; }
}

public sealed record ComputeHealth(bool Configured, HealthLevel Level, double? CpuPercent, string? Detail);

/// <summary>What one node reports about itself at <see cref="NodeHealth.Path"/>.</summary>
public sealed record GatewayHealth(
    bool Configured, HealthLevel Level, int? StorageNodes, int? HealthyStorageNodes, long? UnderReplicated, int? Port, string? Detail);

/// <summary>What one node reports about itself at <see cref="NodeHealth.Path"/>. Gateway is null from nodes predating #135.</summary>
public sealed record NodeHealthReport(string Node, StorageHealth Storage, ComputeHealth Compute, GatewayHealth? Gateway = null);

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

    public NodeHealthReport Local() => new(nodeAddress, Storage(), Compute(), Gateway());

    /// <summary>
    /// Blob gateway: green when it can reach enough storage nodes to keep every object at its copy
    /// target, yellow when it's short of nodes or still repairing, red when it isn't running or has none.
    /// </summary>
    private GatewayHealth Gateway()
    {
        var settings = supervisor.Context.Settings.BlobGateway;
        if (!settings.Enabled)
        {
            return new GatewayHealth(false, HealthLevel.Off, null, null, null, null, null);
        }

        var component = supervisor.BlobGateway;
        var nodes = component.Services?.GetService<Symposia.BlobStorage.Gateway.Nodes.INodeRegistry>();
        var store = component.Services?.GetService<Symposia.BlobStorage.Gateway.Metadata.GatewayMetadataStore>();
        if (component.Status != ComponentStatus.Running || nodes is null || store is null)
        {
            return new GatewayHealth(true, HealthLevel.Bad, null, null, null, settings.Port, component.Reason ?? $"Blob gateway is {component.Status}");
        }

        var all = nodes.All.Count;
        var healthy = nodes.All.Count(n => n.IsHealthy);
        var readOnly = nodes.All.Count(n => n.IsHealthy && !n.IsWritable);
        long? shortCopies = null;
        try
        {
            shortCopies = store.CountUnderReplicated(settings.MinCopies);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Diagnostics only; leave the count unknown.
        }

        // Storage nodes only accept registered callers (issue #140); a host with no storage or
        // compute role isn't registered, so every node refuses its gateway.
        var refused = nodes.All.Count(n => n.LastProbeError?.StartsWith("Unauthenticated", StringComparison.Ordinal) == true);
        if (refused > 0 && healthy == 0)
        {
            return new GatewayHealth(true, HealthLevel.Bad, all, 0, shortCopies, settings.Port,
                supervisor.Context.PrimaryEndpoint is null
                    ? "Storage nodes refuse this gateway: this host isn't registered on the network. Turn on Storage or Compute here."
                    : "Storage nodes refuse this gateway (not authorized). Is this host's registration complete? See On-chain registration.");
        }

        var level = healthy == 0 ? HealthLevel.Bad
            : healthy < settings.MinCopies || shortCopies > 0 || healthy - readOnly < settings.WriteQuorum ? HealthLevel.Warn
            : HealthLevel.Ok;
        var detail = healthy == 0 ? "No reachable storage nodes"
            : healthy < settings.MinCopies ? $"Only {healthy} storage node(s) reachable; objects can't reach {settings.MinCopies} copies"
            : healthy == readOnly ? $"Insufficient writable nodes: all {readOnly} reachable storage node(s) are read-only"
            : shortCopies > 0 ? $"{shortCopies} object(s) waiting for another copy"
            : readOnly > 0 ? $"{healthy} storage node(s) reachable, {readOnly} read-only"
            : null;
        return new GatewayHealth(true, level, all, healthy, shortCopies, settings.Port, detail);
    }

    private StorageHealth Storage()
    {
        var settings = supervisor.Context.Settings.Storage;
        if (!settings.Enabled)
        {
            return new StorageHealth(false, HealthLevel.Off, null, null, null);
        }

        var component = supervisor.Storage;
        var capacity = StorageCapacity();
        if (component.Status != ComponentStatus.Running || capacity is null)
        {
            return new StorageHealth(true, HealthLevel.Bad, settings.MaxCapacityBytes, null, $"Storage is {component.Status}");
        }

        // Free is what the node can back: min(unused quota, free disk − reserve) (#199).
        var free = capacity.Available;
        var (level, detail) = capacity.Mode switch
        {
            StorageMode.Critical => (HealthLevel.Bad, "Critical: free disk is below the reserve"),
            StorageMode.ReadOnly => (HealthLevel.Warn, "Read-only: disk can't cover allocation"),
            _ => (StorageLevel(free, capacity.Allocated), (string?)null),
        };
        // Capacity is what the node can back (used + available), not the allocation it can't (#199 FR10).
        return new StorageHealth(true, level, capacity.Total, free, detail)
        {
            UsedBytes = capacity.Used,
            ReadOnly = capacity.ReadOnly,
            Mode = capacity.Mode.ToString(),
        };
    }

    /// <summary>The running storage role's latest capacity measurement (#197); null when it isn't running.</summary>
    public StorageCapacitySnapshot? StorageCapacity() =>
        supervisor.Storage.Status == ComponentStatus.Running
            ? supervisor.Storage.Services?.GetService<StorageCapacityMonitor>()?.Current
            : null;

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
