using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.Protocol;
using Symposia.BlobStorage.StorageNode.Grpc;
using Symposia.BlobStorage.StorageNode.Storage;
using Symposia.Network.Node.Alerts;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>Read-only storage mode when the disk can't cover the allocation (issue #199).</summary>
public sealed class ReadOnlyModeTests : IDisposable
{
    private const long GB = 1L << 30;
    private const long MB = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym199-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(10, 256)] // 1% = 102 MiB → floor of 256 MiB
    [InlineData(100, 1024)] // 1% of 100 GiB
    [InlineData(4096, 10240)] // capped at 10 GiB
    public void Reserve_IsOnePercent_ClampedTo256MbAnd10Gb(long volumeGb, long expectedMb) =>
        Assert.Equal(expectedMb * MB, ReadOnlyMode.Reserve(volumeGb * GB));

    // Spec example: 5 GB allocated, 3 GB used, 1 GB free → reports ~0.75 GB available of ~3.75 GB, not 5 GB.
    [Fact]
    public void ReportedCapacity_IsWhatTheDiskCanBack()
    {
        var s = new StorageCapacitySnapshot(5 * GB, 3 * GB, 1 * GB, 10 * GB, "/data", DiskLowState.Error, DateTimeOffset.UtcNow, StorageMode.ReadOnly);

        Assert.Equal(256 * MB, s.Reserve);
        Assert.Equal(1 * GB - 256 * MB, s.Available);
        Assert.Equal(4 * GB - 256 * MB, s.Total);
    }

    [Fact]
    public void Mode_EntersReadOnly_AndExitsOnlyPastTheMarginTwice()
    {
        var unused = 2 * GB;
        var reserve = 256 * MB;
        var streak = 0;

        Assert.Equal(StorageMode.Writable, ReadOnlyMode.Evaluate(StorageMode.Writable, 3 * GB, unused, reserve, ref streak));
        Assert.Equal(StorageMode.ReadOnly, ReadOnlyMode.Evaluate(StorageMode.Writable, 2 * GB, unused, reserve, ref streak));

        // Back above W ≥ U but short of 2.10 × U + R: still read-only.
        Assert.Equal(StorageMode.ReadOnly, ReadOnlyMode.Evaluate(StorageMode.ReadOnly, 3 * GB, unused, reserve, ref streak));

        var exit = (long)(2.10 * unused) + reserve + 1;
        Assert.Equal(StorageMode.ReadOnly, ReadOnlyMode.Evaluate(StorageMode.ReadOnly, exit, unused, reserve, ref streak));
        Assert.Equal(StorageMode.Writable, ReadOnlyMode.Evaluate(StorageMode.ReadOnly, exit, unused, reserve, ref streak));
    }

    [Fact]
    public void Mode_ExitStreak_ResetsWhenSpaceDipsAgain()
    {
        var unused = 2 * GB;
        var reserve = 256 * MB;
        var exit = (long)(2.10 * unused) + reserve + 1;
        var streak = 0;

        ReadOnlyMode.Evaluate(StorageMode.ReadOnly, exit, unused, reserve, ref streak);
        ReadOnlyMode.Evaluate(StorageMode.ReadOnly, 3 * GB, unused, reserve, ref streak);
        Assert.Equal(StorageMode.ReadOnly, ReadOnlyMode.Evaluate(StorageMode.ReadOnly, exit, unused, reserve, ref streak));
    }

    [Fact]
    public void Mode_IsCritical_BelowTheReserve_EvenWithAFullAllocation()
    {
        var streak = 0;
        Assert.Equal(StorageMode.Critical, ReadOnlyMode.Evaluate(StorageMode.Writable, 100 * MB, 0, 256 * MB, ref streak));
        Assert.Equal(StorageMode.Writable, ReadOnlyMode.Evaluate(StorageMode.Writable, 300 * MB, 0, 256 * MB, ref streak));
        // Leaving critical falls back to the read-only exit rules.
        Assert.Equal(StorageMode.ReadOnly, ReadOnlyMode.Evaluate(StorageMode.Critical, 3 * GB, 2 * GB, 256 * MB, ref streak));
    }

    [Fact]
    public void Monitor_RaisesReadOnlyThenCritical_AndClearsOnRecovery()
    {
        var disk = new FakeDisk { Free = 4 * GB };
        var sink = new StorageCapacityTests.RecordingSink();
        var monitor = Monitor(disk, sink, allocated: 5 * GB, used: 3 * GB);
        monitor.Evaluate();
        Assert.False(sink.Alerts.ContainsKey(AlertKeys.StorageReadOnly));

        disk.Free = 1 * GB;
        Assert.Equal(StorageMode.ReadOnly, monitor.Evaluate().Mode);
        var alert = sink.Alerts[AlertKeys.StorageReadOnly];
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("Writes resume once free disk reaches", alert.Detail);

        disk.Free = 100 * MB;
        Assert.Equal(StorageMode.Critical, monitor.Evaluate().Mode);
        Assert.Equal(AlertSeverity.Error, sink.Alerts[AlertKeys.StorageReadOnly].Severity);

        disk.Free = 5 * GB;
        monitor.Evaluate();
        Assert.Equal(StorageMode.Writable, monitor.Evaluate().Mode);
        Assert.False(sink.Alerts.ContainsKey(AlertKeys.StorageReadOnly));
    }

    [Fact]
    public void FirstDiskReadFailing_RefusesWrites_WithoutReportingCritical()
    {
        var sink = new StorageCapacityTests.RecordingSink();
        var monitor = Monitor(new ThrowingDisk(), sink, allocated: 5 * GB, used: 3 * GB);

        var s = monitor.Evaluate();

        Assert.Equal(StorageMode.ReadOnly, s.Mode);
        Assert.False(monitor.TryReserve(0));
        Assert.Empty(sink.Alerts);
    }

    [Fact]
    public async Task StoppingTheRole_LeavesAlertsStanding_ForTheHostToCarryAcrossARestart()
    {
        var disk = new FakeDisk { Free = 1 * GB };
        var sink = new StorageCapacityTests.RecordingSink();
        var monitor = Monitor(disk, sink, allocated: 5 * GB, used: 3 * GB);
        monitor.Evaluate();

        await monitor.StopAsync(CancellationToken.None);

        Assert.True(sink.Alerts.ContainsKey(AlertKeys.StorageReadOnly));
        Assert.True(sink.Alerts.ContainsKey(AlertKeys.StorageDiskLow));
    }

    [Fact]
    public void DiskFull_IsRecognisedByErrno_NotByUnrelatedCodes()
    {
        var enospc = new IOException("No space left on device", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);
        var enotempty = new IOException("Directory not empty", 39);

        Assert.True(StorageNodeGrpcService.IsDiskFull(enospc));
        Assert.Equal(OperatingSystem.IsWindows(), StorageNodeGrpcService.IsDiskFull(enotempty));
    }

    [Fact]
    public void Admission_RefusesWhileReadOnly_AndBeyondWhatTheDiskCanBack()
    {
        var disk = new FakeDisk { Free = 4 * GB };
        var monitor = Monitor(disk, new StorageCapacityTests.RecordingSink(), allocated: 5 * GB, used: 3 * GB);
        monitor.Evaluate();

        Assert.True(monitor.TryReserve(1 * GB));
        Assert.False(monitor.TryReserve(1536 * MB)); // 1 GB in flight + 1.5 GB > 2 GB unused
        monitor.Release(1 * GB, 0);
        Assert.True(monitor.TryReserve(1536 * MB));
        monitor.Release(1536 * MB, 0);

        disk.Free = 1 * GB;
        monitor.Evaluate();
        Assert.False(monitor.TryReserve(0));
    }

    [Fact]
    public async Task ReadOnlyNode_RefusesWrites_ServesReads_AndProbesHealthy()
    {
        using var factory = new ReadOnlyNodeFactory();
        var client = Client(factory);
        var payload = "kept while read-only"u8.ToArray();
        var cid = await Write(client, payload);

        factory.Disk.Free = 100 * MB + 256 * MB; // W = 100 MiB < unused (10 GiB default allocation)
        factory.Services.GetRequiredService<StorageCapacityMonitor>().Evaluate();

        var refused = await Assert.ThrowsAsync<RpcException>(() => Write(client, "new data"u8.ToArray()));
        Assert.Equal(StatusCode.ResourceExhausted, refused.StatusCode);
        Assert.Equal(StorageNodeErrors.ReadOnly, refused.Trailers.GetValue(StorageNodeErrors.ReasonTrailer));

        var read = client.ReadBlob(new ReadBlobRequest { Cid = cid });
        var bytes = new List<byte>();
        await foreach (var chunk in read.ResponseStream.ReadAllAsync())
        {
            bytes.AddRange(chunk.Data);
        }

        Assert.Equal(payload, bytes);

        var probe = await client.ProbeAsync(new ProbeRequest());
        Assert.True(probe.Healthy);
        Assert.True(probe.ReadOnly);
        Assert.Equal(100 * MB, probe.AvailableStorageBytes);
        Assert.Equal(256 * MB, probe.ReserveBytes);

        Assert.True((await client.DeleteBlobAsync(new DeleteBlobRequest { Cid = cid })).Deleted);
    }

    [Fact]
    public async Task CriticalNode_ProbesUnhealthy_AndStillServesReads()
    {
        using var factory = new ReadOnlyNodeFactory();
        var client = Client(factory);
        var cid = await Write(client, "at risk"u8.ToArray());

        factory.Disk.Free = 10 * MB;
        factory.Services.GetRequiredService<StorageCapacityMonitor>().Evaluate();

        var probe = await client.ProbeAsync(new ProbeRequest());
        Assert.False(probe.Healthy);
        var challenge = await client.IntegrityChallengeAsync(new IntegrityChallengeRequest { Cid = cid });
        Assert.Equal(cid, challenge.Sha256Hex);
    }

    private static Protocol.StorageNode.StorageNodeClient Client(WebApplicationFactory<Program> factory)
    {
        var http = factory.CreateClient();
        return new Protocol.StorageNode.StorageNodeClient(GrpcChannel.ForAddress(http.BaseAddress!, new GrpcChannelOptions { HttpClient = http }));
    }

    private static async Task<string> Write(Protocol.StorageNode.StorageNodeClient client, byte[] payload, long expected = 0)
    {
        var call = client.WriteBlob();
        await call.RequestStream.WriteAsync(new WriteBlobChunk
        {
            Header = new WriteBlobHeader { TenantId = "t", Bucket = "b", Key = "k", ExpectedSizeBytes = expected },
        });
        try
        {
            await call.RequestStream.WriteAsync(new WriteBlobChunk { Data = ByteString.CopyFrom(payload) });
            await call.RequestStream.CompleteAsync();
        }
        catch (RpcException)
        {
            // The node ended the call; its status is on the response.
        }
        catch (InvalidOperationException)
        {
        }

        return (await call.ResponseAsync).Cid;
    }

    private StorageCapacityMonitor Monitor(IDiskSpace disk, IOperatorAlertSink sink, long allocated, long used)
    {
        var store = new ManifestStore(Options.Create(new StorageNodeOptions { ManifestDbPath = Path.Combine(_dir, "manifest.db") }));
        store.Initialize();
        store.Upsert(new BlobRecord(Cid.FromHash(SHA256.HashData("used"u8)), used, "t", "b", "k", [], DateTimeOffset.UtcNow, null, BlobStatus.Active));
        var options = Options.Create(new StorageNodeOptions { StorageRoot = Path.Combine(_dir, "blobs"), MaxCapacityBytes = allocated });
        return new StorageCapacityMonitor(store, options, disk, sink, NullLogger<StorageCapacityMonitor>.Instance);
    }

    private sealed class ThrowingDisk : IDiskSpace
    {
        public (long Free, long Total) Measure(string path) => throw new IOException("stat failed");
    }

    internal sealed class FakeDisk : IDiskSpace
    {
        public long Free { get; set; } = 500 * GB;

        public long Total { get; set; } = 10 * GB;

        public (long Free, long Total) Measure(string path) => (Free, Total);
    }

    private sealed class ReadOnlyNodeFactory : WebApplicationFactory<Program>
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"symposia-ro-test-{Guid.NewGuid():N}");

        public FakeDisk Disk { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageNode:StorageRoot"] = Path.Combine(_dataDir, "blobs"),
                ["StorageNode:ManifestDbPath"] = Path.Combine(_dataDir, "manifest.db"),
                ["StorageNode:NodeIdentityKeyPath"] = Path.Combine(_dataDir, "node-identity.pem"),
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<IDiskSpace>(Disk));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, recursive: true);
            }
        }
    }
}
