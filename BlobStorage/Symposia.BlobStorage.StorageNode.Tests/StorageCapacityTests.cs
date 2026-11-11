using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.StorageNode.Storage;
using Symposia.Network.Node.Alerts;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>Used-bytes counter and the low-disk-vs-allocation alert (issue #197).</summary>
public sealed class StorageCapacityTests : IDisposable
{
    private const long GB = 1L << 30;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym197-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(4.5, DiskLowState.Ok)]
    [InlineData(3.5, DiskLowState.Warning)]
    [InlineData(1.9, DiskLowState.Error)]
    [InlineData(2.0, DiskLowState.Warning)]
    [InlineData(4.0, DiskLowState.Ok)]
    public void Thresholds_AgainstTwoGbUnused(double freeGb, DiskLowState expected) =>
        Assert.Equal(expected, DiskLow.Evaluate(DiskLowState.Ok, (long)(freeGb * GB), 2 * GB));

    [Fact]
    public void Recovery_NeedsFivePercentClearance()
    {
        var unused = 2 * GB;
        Assert.Equal(DiskLowState.Warning, DiskLow.Evaluate(DiskLowState.Warning, (long)(4.1 * GB), unused));
        Assert.Equal(DiskLowState.Ok, DiskLow.Evaluate(DiskLowState.Warning, (long)(4.3 * GB), unused));
        Assert.Equal(DiskLowState.Error, DiskLow.Evaluate(DiskLowState.Error, (long)(2.05 * GB), unused));
        Assert.Equal(DiskLowState.Warning, DiskLow.Evaluate(DiskLowState.Error, (long)(2.2 * GB), unused));
        // Getting worse applies at once.
        Assert.Equal(DiskLowState.Error, DiskLow.Evaluate(DiskLowState.Warning, (long)(1.99 * GB), unused));
    }

    [Fact]
    public void FullyUsedAllocation_IsNeverLow() =>
        Assert.Equal(DiskLowState.Ok, DiskLow.Evaluate(DiskLowState.Error, 0, 0));

    [Fact]
    public void UsedBytesCounter_TracksUpsertReplaceAndDelete()
    {
        var store = Manifest();
        var a = Cid.FromHash(SHA256.HashData("a"u8));
        var b = Cid.FromHash(SHA256.HashData("b"u8));

        store.Upsert(Record(a, 100));
        store.Upsert(Record(b, 50));
        store.Upsert(Record(a, 100)); // re-written: no double count
        Assert.Equal(150, store.UsedBytes);

        store.Delete(a);
        store.Delete(a);
        Assert.Equal(50, store.UsedBytes);
        Assert.Equal(0, store.ReconcileUsedBytes());

        var reopened = Manifest();
        Assert.Equal(50, reopened.UsedBytes);
    }

    [Fact]
    public void Monitor_RaisesEscalatesAndClears_TheDiskLowAlert()
    {
        var disk = new FakeDisk { Free = (long)(4.5 * GB) };
        var sink = new RecordingSink();
        var monitor = Monitor(disk, sink, allocated: 5 * GB, used: 3 * GB);

        Assert.Equal(DiskLowState.Ok, monitor.Evaluate().DiskLow);
        Assert.Null(sink.Current);

        disk.Free = (long)(3.5 * GB);
        var s = monitor.Evaluate();
        Assert.Equal(DiskLowState.Warning, s.DiskLow);
        Assert.Equal((AlertSeverity.Warning, "Low disk space"), (sink.Current!.Value.Severity, sink.Current.Value.Title));
        Assert.Contains("3.5 GB free", sink.Current.Value.Detail);
        Assert.Contains("2.0 GB of allocated storage still unused", sink.Current.Value.Detail);
        Assert.Contains("Warning below 4.0 GB, error below 2.0 GB", sink.Current.Value.Detail);

        disk.Free = (long)(1.9 * GB);
        monitor.Evaluate();
        Assert.Equal(AlertSeverity.Error, sink.Current!.Value.Severity);
        Assert.Contains("read-only", sink.Current.Value.Detail);

        disk.Free = 5 * GB;
        monitor.Evaluate();
        Assert.Null(sink.Current);
    }

    [Fact]
    public void Monitor_KeepsLastReading_WhenTheDiskCantBeRead()
    {
        var disk = new FakeDisk { Free = (long)(3.5 * GB) };
        var sink = new RecordingSink();
        var monitor = Monitor(disk, sink, allocated: 5 * GB, used: 3 * GB);
        monitor.Evaluate();

        disk.Throw = true;
        var s = monitor.Evaluate();

        Assert.Equal(DiskLowState.Warning, s.DiskLow);
        Assert.Equal(AlertSeverity.Warning, sink.Current!.Value.Severity);
    }

    [Fact]
    public void DriveDiskSpace_FollowsASymlinkedRoot()
    {
        var target = Directory.CreateDirectory(Path.Combine(_dir, "target")).FullName;
        var link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, target);

        var (free, total) = new DriveDiskSpace().Measure(link);

        Assert.Equal(new DriveInfo(target).TotalSize, total);
        Assert.True(free > 0);
    }

    private ManifestStore Manifest()
    {
        var store = new ManifestStore(Options.Create(new StorageNodeOptions { ManifestDbPath = Path.Combine(_dir, "manifest.db") }));
        store.Initialize();
        return store;
    }

    private static BlobRecord Record(Cid cid, long size) =>
        new(cid, size, "t", "b", "k", [], DateTimeOffset.UtcNow, ChecksumVerifiedAt: null, BlobStatus.Active);

    private StorageCapacityMonitor Monitor(IDiskSpace disk, IOperatorAlertSink sink, long allocated, long used)
    {
        var store = Manifest();
        store.Upsert(Record(Cid.FromHash(SHA256.HashData("used"u8)), used));
        var options = Options.Create(new StorageNodeOptions { StorageRoot = Path.Combine(_dir, "blobs"), MaxCapacityBytes = allocated });
        return new StorageCapacityMonitor(store, options, disk, sink, NullLogger<StorageCapacityMonitor>.Instance);
    }

    private sealed class FakeDisk : IDiskSpace
    {
        public long Free { get; set; }

        public long Total { get; set; } = 100L * GB;

        public bool Throw { get; set; }

        public (long Free, long Total) Measure(string path) => Throw ? throw new IOException("gone") : (Free, Total);
    }

    internal sealed class RecordingSink : IOperatorAlertSink
    {
        public Dictionary<string, (AlertSeverity Severity, string Title, string Detail)> Alerts { get; } = [];

        public (AlertSeverity Severity, string Title, string Detail)? Current =>
            Alerts.TryGetValue(AlertKeys.StorageDiskLow, out var a) ? a : null;

        public void Raise(string key, AlertSeverity severity, string title, string detail) => Alerts[key] = (severity, title, detail);

        public void Clear(string key) => Alerts.Remove(key);
    }
}
