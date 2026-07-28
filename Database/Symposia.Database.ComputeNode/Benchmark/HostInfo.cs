namespace Symposia.Database.ComputeNode.Benchmark;

/// <summary>
/// What the daemon's own OS-level probe observes on this machine, independent of whatever
/// cores/RAM the operator declared at onboarding (#90). This is the cross-check input for
/// FR8 -- a witness compares this against the operator's declared values, not the node itself.
/// </summary>
public sealed record HostInfo(int PhysicalCores, int TotalRamGB, string CacheDiskDevice, string CacheDiskType);
