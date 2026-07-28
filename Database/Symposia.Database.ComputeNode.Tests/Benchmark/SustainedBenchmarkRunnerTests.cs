using Microsoft.Extensions.Options;
using Symposia.Database.ComputeNode;
using Symposia.Database.ComputeNode.Benchmark;

namespace Symposia.Database.ComputeNode.Tests.Benchmark;

/// <summary>
/// Traces to QA test plan Group 7 (sustained-duration / burstable CPU detection). Uses tiny
/// window/interval/discard values so the discard-then-average behavior is exercised in
/// milliseconds rather than the real 90s default.
/// </summary>
public sealed class SustainedBenchmarkRunnerTests
{
    private static SustainedBenchmarkRunner CreateRunner(IWorkloadSampler sampler, int windowSec, int discardSec, int intervalSec)
    {
        var options = Options.Create(new ComputeNodeOptions
        {
            BenchmarkSustainedWindowSeconds = windowSec,
            BenchmarkBurstDiscardSeconds = discardSec,
            BenchmarkSampleIntervalSeconds = intervalSec,
        });
        return new SustainedBenchmarkRunner(options, sampler);
    }

    [Fact]
    public async Task RunAsync_BurstThenDecay_ReportsSustainedRate_NotBurstRate()
    {
        // Samples at t=0 and t=1 (burst, discarded) are far higher than t=2 and t=3 (sustained).
        var sampler = new FakeWorkloadSampler(mipsSequence: [5000, 5000, 800, 800]);
        var runner = CreateRunner(sampler, windowSec: 4, discardSec: 2, intervalSec: 1);

        var report = await runner.RunAsync();

        Assert.Equal(800, report.Mips);
    }

    [Fact]
    public async Task RunAsync_NonBurstableHardware_SustainsRateAcrossFullWindow()
    {
        var sampler = new FakeWorkloadSampler(mipsSequence: [2500, 2500, 2500, 2500]);
        var runner = CreateRunner(sampler, windowSec: 4, discardSec: 2, intervalSec: 1);

        var report = await runner.RunAsync();

        Assert.Equal(2500, report.Mips);
    }

    [Fact]
    public async Task RunAsync_ReportsConfiguredWindowAndDiscardDurations()
    {
        var sampler = new FakeWorkloadSampler(mipsSequence: [1000]);
        var runner = CreateRunner(sampler, windowSec: 2, discardSec: 1, intervalSec: 1);

        var report = await runner.RunAsync();

        Assert.Equal(2, report.SampleWindowSec);
        Assert.Equal(1, report.BurstDiscardSec);
    }

    [Fact]
    public async Task RunAsync_DiscardCoveringFullWindow_FallsBackRatherThanDividingByZero()
    {
        var sampler = new FakeWorkloadSampler(mipsSequence: [1234]);
        var runner = CreateRunner(sampler, windowSec: 1, discardSec: 1, intervalSec: 1);

        var report = await runner.RunAsync();

        Assert.Equal(1234, report.Mips);
    }
}
