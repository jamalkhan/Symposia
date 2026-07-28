using Symposia.Database.ComputeNode.Benchmark;

namespace Symposia.Database.ComputeNode.Tests.Benchmark;

/// <summary>
/// Deterministic sampler double. Each Sample* call returns the next queued value (or the last
/// queued value once exhausted), letting tests script a burst-then-decay curve without waiting
/// on real sustained-window timing.
/// </summary>
public sealed class FakeWorkloadSampler : IWorkloadSampler
{
    private readonly List<double> _mipsSequence;
    private readonly List<double> _ramBwSequence;
    private readonly List<double> _iopsSequence;
    private int _mipsCall;
    private int _ramBwCall;
    private int _iopsCall;

    public FakeWorkloadSampler(IEnumerable<double> mipsSequence, IEnumerable<double>? ramBwSequence = null, IEnumerable<double>? iopsSequence = null)
    {
        _mipsSequence = mipsSequence.ToList();
        _ramBwSequence = (ramBwSequence ?? [10.0]).ToList();
        _iopsSequence = (iopsSequence ?? [10_000.0]).ToList();
    }

    public double SampleMips() => Next(_mipsSequence, ref _mipsCall);

    public double SampleRamBandwidthGBs() => Next(_ramBwSequence, ref _ramBwCall);

    public double SampleIopsRandomRead() => Next(_iopsSequence, ref _iopsCall);

    private static double Next(List<double> sequence, ref int cursor)
    {
        var value = sequence[Math.Min(cursor, sequence.Count - 1)];
        cursor++;
        return value;
    }
}
