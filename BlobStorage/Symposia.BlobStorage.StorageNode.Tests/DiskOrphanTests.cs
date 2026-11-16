using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.StorageNode.Storage;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>The node's disk-orphan pass never deletes, and never touches a file younger than 48 h (#16 FR5.2–5.4).</summary>
public sealed class DiskOrphanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym16-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task UnlistedFile_IsLeftAlone_Then_RecordedAsOrphan_NeverDeleted()
    {
        var options = Options.Create(new StorageNodeOptions { StorageRoot = Path.Combine(_dir, "blobs"), ManifestDbPath = Path.Combine(_dir, "m.db") });
        var blobs = new LocalBlobStore(options);
        var manifest = new ManifestStore(options);
        manifest.Initialize();
        var (cid, _) = await blobs.WriteAsync(new MemoryStream("never committed"u8.ToArray()), CancellationToken.None);
        var worker = new IntegritySelfCheckWorker(blobs, manifest, options, NullLogger<IntegritySelfCheckWorker>.Instance);

        Assert.Equal(0, worker.RunDiskOrphanPass()); // young: untouched
        Assert.Null(manifest.Get(cid));

        var file = Directory.EnumerateFiles(Path.Combine(_dir, "blobs"), cid.Value + ".blob", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-49));

        Assert.Equal(1, worker.RunDiskOrphanPass());
        Assert.Equal(BlobStatus.Orphaned, manifest.Get(cid)!.Status);
        Assert.True(blobs.Exists(cid)); // the coordination layer decides deletion
    }
}
