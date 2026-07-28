namespace Symposia.Database.ComputeNode.Benchmark;

/// <summary>
/// One instantaneous reading of each of the three locally-measured dimensions (RTT-to-peers is
/// witness-measured, not sampled here -- see the architectural plan). Abstracted so
/// <see cref="SustainedBenchmarkRunner"/>'s discard-then-average logic is testable against a
/// synthetic burst-then-decay curve without running real sustained-window benchmarks in CI.
/// </summary>
public interface IWorkloadSampler
{
    double SampleMips();

    double SampleRamBandwidthGBs();

    double SampleIopsRandomRead();
}
