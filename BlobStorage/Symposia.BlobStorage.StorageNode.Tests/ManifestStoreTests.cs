using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.StorageNode.Storage;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>Issue #179: ListByStatus selects cid first, so its rows must map with the same fields as Get.</summary>
public sealed class ManifestStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym179-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ListByStatus_MapsTheSameRecordAsGet()
    {
        var store = new ManifestStore(Options.Create(new StorageNodeOptions { ManifestDbPath = Path.Combine(_dir, "manifest.db") }));
        store.Initialize();
        var cid = Cid.FromHash(SHA256.HashData("hello"u8));
        var stored = new DateTimeOffset(2026, 11, 4, 5, 3, 4, TimeSpan.Zero);
        store.Upsert(new BlobRecord(cid, 15, "dev", "proof", "hello.txt", [], stored, ChecksumVerifiedAt: null, BlobStatus.Active));

        var listed = Assert.Single(store.ListByStatus(BlobStatus.Active));

        Assert.Equal(cid, listed.Cid);
        Assert.Equal(15, listed.SizeBytes);
        Assert.Equal("dev", listed.TenantId);
        Assert.Equal("proof", listed.Bucket);
        Assert.Equal("hello.txt", listed.Key);
        Assert.Empty(listed.RegionTags);
        Assert.Equal(stored, listed.StoredAt);
        Assert.Null(listed.ChecksumVerifiedAt);
        Assert.Equal(store.Get(cid)!.StoredAt, listed.StoredAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
