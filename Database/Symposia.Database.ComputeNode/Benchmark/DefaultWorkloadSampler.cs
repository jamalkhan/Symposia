using Microsoft.Extensions.Options;

namespace Symposia.Database.ComputeNode.Benchmark;

/// <summary>
/// Real, single-threaded workload sampler run by <see cref="SustainedBenchmarkRunner"/>. Each
/// Sample* call takes one short, self-timed reading: a fixed-duration integer-op loop for MIPS
/// (Dhrystone/Coremark-class per the architectural plan), a large-array copy for RAM bandwidth
/// (STREAM-style), and timed random 4K reads against the configured pageserver cache volume
/// path for IOPS -- against the actual configured cache path, not an arbitrary scratch file, so a
/// node can't benchmark a fast disk while running the cache on something slower.
/// </summary>
public sealed class DefaultWorkloadSampler : IWorkloadSampler
{
    private const int SampleDurationMs = 200;
    private const int RamBufferLongs = 8 * 1024 * 1024; // 64MB of longs
    private const int IoBlockSizeBytes = 4096;
    private const int IoFileSizeBytes = 16 * 1024 * 1024;

    private readonly string _cacheProbeFilePath;

    public DefaultWorkloadSampler(IOptions<ComputeNodeOptions> options)
    {
        var cacheRoot = Path.GetFullPath(options.Value.DataRoot);
        Directory.CreateDirectory(cacheRoot);
        _cacheProbeFilePath = Path.Combine(cacheRoot, ".benchmark-iops-probe");
    }

    public double SampleMips()
    {
        long ops = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = TimeSpan.FromMilliseconds(SampleDurationMs);
        long acc = 1;
        while (sw.Elapsed < deadline)
        {
            for (var i = 0; i < 100_000; i++)
            {
                acc = (acc * 1_103_515_245 + 12_345) & 0x7FFFFFFF;
            }
            ops += 100_000;
        }
        sw.Stop();
        _ = acc; // prevent dead-code elimination of the loop

        return ops / sw.Elapsed.TotalSeconds / 1_000_000.0;
    }

    public double SampleRamBandwidthGBs()
    {
        var src = new long[RamBufferLongs];
        var dst = new long[RamBufferLongs];
        for (var i = 0; i < RamBufferLongs; i++) src[i] = i;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Array.Copy(src, dst, RamBufferLongs);
        sw.Stop();

        var bytesCopied = (double)RamBufferLongs * sizeof(long);
        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.000_001);
        return bytesCopied / seconds / 1_073_741_824.0;
    }

    public double SampleIopsRandomRead()
    {
        EnsureProbeFile();

        var buffer = new byte[IoBlockSizeBytes];
        var random = Random.Shared;
        var maxOffsetBlocks = IoFileSizeBytes / IoBlockSizeBytes;

        using var stream = new FileStream(_cacheProbeFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long reads = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = TimeSpan.FromMilliseconds(SampleDurationMs);
        while (sw.Elapsed < deadline)
        {
            stream.Seek((long)random.Next(maxOffsetBlocks) * IoBlockSizeBytes, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, IoBlockSizeBytes);
            reads++;
        }
        sw.Stop();

        return reads / sw.Elapsed.TotalSeconds;
    }

    private void EnsureProbeFile()
    {
        if (File.Exists(_cacheProbeFilePath) && new FileInfo(_cacheProbeFilePath).Length >= IoFileSizeBytes)
            return;

        using var stream = new FileStream(_cacheProbeFilePath, FileMode.Create, FileAccess.Write);
        stream.SetLength(IoFileSizeBytes);
    }
}
