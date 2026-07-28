namespace Symposia.Database.ComputeNode.Benchmark;

/// <summary>
/// Raw result of POST /benchmark/run -- measurements only, no tier classification. The daemon
/// executes and reports; it never scores or classifies its own results (that happens on the
/// witness/registry side per issue #89's architectural plan, which is what makes "not
/// self-reported" architectural rather than a policy the node is trusted to follow).
/// </summary>
public sealed record BenchmarkReport(
    double Mips,
    double RamBandwidthGBs,
    double IopsRandomRead,
    int SampleWindowSec,
    int BurstDiscardSec);
