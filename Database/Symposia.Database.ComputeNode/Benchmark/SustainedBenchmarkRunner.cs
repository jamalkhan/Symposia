using Microsoft.Extensions.Options;

namespace Symposia.Database.ComputeNode.Benchmark;

// System.Linq is provided via the project's ImplicitUsings.

/// <summary>
/// Implements POST /benchmark/run's sustained-measurement methodology (issue #89 FR9): samples
/// each dimension every BenchmarkSampleIntervalSeconds across BenchmarkSustainedWindowSeconds,
/// discards the leading BenchmarkBurstDiscardSeconds as a burst-absorption window, and reports
/// the trailing sustained average. A burstable/shared-vCPU instance can look good for its
/// credit-burst duration, but the sustained tail collapses once discarded; the discard window is
/// sized (by config) to exceed typical burst-credit exhaustion windows.
/// </summary>
public sealed class SustainedBenchmarkRunner(IOptions<ComputeNodeOptions> options, IWorkloadSampler sampler)
{
    private readonly ComputeNodeOptions _options = options.Value;

    public async Task<BenchmarkReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var interval = TimeSpan.FromSeconds(_options.BenchmarkSampleIntervalSeconds);
        var totalWindow = TimeSpan.FromSeconds(_options.BenchmarkSustainedWindowSeconds);
        var discard = TimeSpan.FromSeconds(_options.BenchmarkBurstDiscardSeconds);

        var mipsSamples = new List<double>();
        var ramBwSamples = new List<double>();
        var iopsSamples = new List<double>();

        var elapsed = TimeSpan.Zero;
        while (elapsed < totalWindow)
        {
            var mips = sampler.SampleMips();
            var ramBw = sampler.SampleRamBandwidthGBs();
            var iops = sampler.SampleIopsRandomRead();

            if (elapsed >= discard)
            {
                mipsSamples.Add(mips);
                ramBwSamples.Add(ramBw);
                iopsSamples.Add(iops);
            }

            elapsed += interval;
            if (elapsed < totalWindow)
                await Task.Delay(interval, cancellationToken);
        }

        // A discard window covering the entire sustained window is a misconfiguration, not a
        // condition callers should silently mask -- fall back to all samples rather than
        // dividing by zero, but this indicates BenchmarkBurstDiscardSeconds needs retuning.
        if (mipsSamples.Count == 0)
        {
            mipsSamples.Add(sampler.SampleMips());
            ramBwSamples.Add(sampler.SampleRamBandwidthGBs());
            iopsSamples.Add(sampler.SampleIopsRandomRead());
        }

        return new BenchmarkReport(
            mipsSamples.Average(),
            ramBwSamples.Average(),
            iopsSamples.Average(),
            _options.BenchmarkSustainedWindowSeconds,
            _options.BenchmarkBurstDiscardSeconds);
    }
}
