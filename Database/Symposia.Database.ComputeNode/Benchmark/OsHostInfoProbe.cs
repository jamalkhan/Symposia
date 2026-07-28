using Microsoft.Extensions.Options;

namespace Symposia.Database.ComputeNode.Benchmark;

/// <summary>
/// Reads what the OS actually reports for this machine, per the architectural plan's
/// "GET /hostinfo -- a read of what the OS actually reports, independent of the operator's
/// declared values" (issue #89). Logical processor count is used as the physical-core reading
/// (a hyperthreading-aware split is a documented follow-on, not required for FR8's
/// cross-check purpose); disk type detection is best-effort and reported as "unknown" when the
/// platform doesn't expose it cheaply.
/// </summary>
public sealed class OsHostInfoProbe(IOptions<ComputeNodeOptions> options) : IHostInfoProbe
{
    private readonly ComputeNodeOptions _options = options.Value;

    public HostInfo Probe()
    {
        var cores = Environment.ProcessorCount;
        var totalRamGB = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_073_741_824L);

        var cacheRoot = Path.GetFullPath(_options.DataRoot);
        Directory.CreateDirectory(cacheRoot);
        var device = new DriveInfo(Path.GetPathRoot(cacheRoot) ?? cacheRoot).Name;

        return new HostInfo(cores, totalRamGB, device, "unknown");
    }
}
