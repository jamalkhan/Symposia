using Microsoft.Data.Sqlite;
using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Code-review fixes for multipart and GC bookkeeping (#5, #137 review).</summary>
public sealed class MultipartStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym5s-" + Guid.NewGuid().ToString("N"));
    private readonly GatewayMetadataStore _store;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public MultipartStoreTests()
    {
        _store = new GatewayMetadataStore(Microsoft.Extensions.Options.Options.Create(new GatewayOptions { MetadataDbPath = Path.Combine(_dir, "meta.db") }));
        _store.Initialize();
    }

    private MultipartUpload Upload(string id = "up1", string tenant = "t", string key = "k", DateTimeOffset? expires = null) =>
        new(id, tenant, "b", key, "application/octet-stream", null, null, _now, expires ?? _now.AddDays(7), MultipartStatus.InProgress, null, null);

    private MultipartPart Part(string upload, int number, string cid) =>
        new(upload, number, cid, 10, ["0xA", "0xB"], null, _now);

    private static ObjectRecord Obj(string key, string cid, params string[] nodes) =>
        new("b", key, cid, 10, "application/octet-stream", DateTimeOffset.UtcNow, nodes);

    private HashSet<string> Queued() => _store.GetPendingDeletions(1000).Select(d => $"{d.Cid}@{d.NodeUrl}").ToHashSet();

    [Fact]
    public void StaleCompletingLease_IsReleasedByTheReaper_AndExpiredUploadsAreAbortedWithTheirPartsQueued()
    {
        _store.CreateUpload(Upload("crashed"));
        _store.PutPart(Part("crashed", 1, "c1"));
        Assert.True(_store.TransitionUpload("crashed", MultipartStatus.InProgress, MultipartStatus.Completing));
        _store.CreateUpload(Upload("abandoned", key: "k2", expires: _now.AddMinutes(-1)));
        _store.PutPart(Part("abandoned", 1, "c2"));

        var (released, expired, _) = _store.ReapUploads(_now.AddHours(2), TimeSpan.FromHours(1), TimeSpan.FromDays(7));

        Assert.Equal((1, 1), (released, expired));
        Assert.Equal(MultipartStatus.InProgress, _store.GetUpload("crashed")!.Status);
        Assert.Equal(MultipartStatus.Aborted, _store.GetUpload("abandoned")!.Status);
        Assert.Empty(_store.ListParts("abandoned"));
        Assert.Contains("c2@0xA", Queued());
        Assert.DoesNotContain("c1@0xA", Queued()); // still referenced by the released upload
    }

    [Fact]
    public void FreshCompletingLease_IsNotReleased()
    {
        _store.CreateUpload(Upload());
        _store.TransitionUpload("up1", MultipartStatus.InProgress, MultipartStatus.Completing);
        Assert.Equal(0, _store.ReapUploads(_now, TimeSpan.FromHours(1), TimeSpan.FromDays(7)).Released);
        Assert.Equal(MultipartStatus.Completing, _store.GetUpload("up1")!.Status);
    }

    [Fact]
    public void FinishedUploads_ArePurgedAfterRetention()
    {
        _store.CreateUpload(Upload());
        _store.AbortUpload("up1", TimeSpan.FromHours(1));
        Assert.Equal(1, _store.ReapUploads(_now.AddDays(8), TimeSpan.FromHours(1), TimeSpan.FromDays(7)).Purged);
        Assert.Null(_store.GetUpload("up1"));
    }

    [Fact]
    public void Complete_RequiresTheLease_AndHonoursIfNoneMatch()
    {
        _store.CreateUpload(Upload());
        _store.PutPart(Part("up1", 1, "p1"));
        Assert.Equal(CompleteOutcome.LostLease, _store.CompleteUpload("up1", Obj("k", "final", "0xA"), "e-1", "[]", ifNoneMatch: false));

        _store.TransitionUpload("up1", MultipartStatus.InProgress, MultipartStatus.Completing);
        _store.PutObject(Obj("k", "existing", "0xA"));
        Assert.Equal(CompleteOutcome.PreconditionFailed, _store.CompleteUpload("up1", Obj("k", "final", "0xA"), "e-1", "[]", ifNoneMatch: true));
        Assert.Equal("existing", _store.GetObject("b", "k")!.Cid);

        Assert.Equal(CompleteOutcome.Committed, _store.CompleteUpload("up1", Obj("k", "final", "0xA"), "e-1", "[]", ifNoneMatch: false));
        Assert.Equal("e-1", _store.GetObject("b", "k")!.ETag);
        Assert.Empty(_store.ListParts("up1"));
        // The overwritten object's blob and the consumed part are both unreferenced now.
        Assert.Contains("existing@0xA", Queued());
        Assert.Contains("p1@0xB", Queued());
    }

    [Fact]
    public void AbortingAStaleCompletingUpload_Works_ButNotAFreshOne()
    {
        _store.CreateUpload(Upload());
        _store.TransitionUpload("up1", MultipartStatus.InProgress, MultipartStatus.Completing);
        Assert.False(_store.AbortUpload("up1", TimeSpan.FromHours(1)));
        Assert.True(_store.AbortUpload("up1", TimeSpan.Zero - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ReReferencingABlob_CancelsItsQueuedDeletion()
    {
        _store.EnqueueNodeDeletion("shared", "0xA");
        _store.CreateUpload(Upload());
        _store.PutPart(Part("up1", 1, "shared"));
        Assert.DoesNotContain("shared@0xA", Queued());

        _store.EnqueueNodeDeletion("obj", "0xA");
        _store.PutObject(Obj("k9", "obj", "0xA"));
        Assert.DoesNotContain("obj@0xA", Queued());
    }

    [Fact]
    public void QueuedDeletions_WaitForTheMinimumAge()
    {
        _store.EnqueueNodeDeletion("young", "0xA");
        Assert.Empty(_store.GetPendingDeletions(10, TimeSpan.FromMinutes(15)));
        Assert.Single(_store.GetPendingDeletions(10, TimeSpan.Zero));
    }

    [Fact]
    public void ReplacingAPart_QueuesTheOldBlob()
    {
        _store.CreateUpload(Upload());
        _store.PutPart(Part("up1", 1, "old"));
        var (_, replaced) = _store.PutPart(Part("up1", 1, "new"));
        Assert.Equal("old", replaced!.Cid);
        Assert.Contains("old@0xA", Queued());
    }

    [Fact]
    public void ListUploads_FiltersByTenantInTheQuery_PrefixIsCaseSensitive_KeyMarkerIsExclusive()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.CreateUpload(Upload($"other{i}", tenant: "other", key: $"a/{i}"));
        }

        _store.CreateUpload(Upload("mine1", key: "a/x"));
        _store.CreateUpload(Upload("mine2", key: "A/y"));
        _store.CreateUpload(Upload("mine3", key: "a/z"));

        Assert.Equal(["mine1", "mine3"], _store.ListUploads("t", "b", "a/", null, null, 2, _now).Select(u => u.UploadId));
        Assert.Equal(["mine3"], _store.ListUploads("t", "b", "a/", "a/x", null, 10, _now).Select(u => u.UploadId));
        Assert.Equal(["mine2"], _store.ListUploads("t", "b", "A/", null, null, 10, _now).Select(u => u.UploadId));
    }

    [Fact]
    public void RenamingANodeUrl_CoversPartsAndCollapsesDuplicateDeletions()
    {
        _store.CreateUpload(Upload());
        _store.PutPart(new MultipartPart("up1", 1, "p", 10, ["http://n:6180"], null, _now));
        _store.EnqueueNodeDeletion("x", "http://n:6180");
        _store.EnqueueNodeDeletion("x", "0xN");

        _store.RenameReplicaUrl("http://n:6180", "0xN");

        Assert.Equal(["0xN"], _store.ListParts("up1").Single().NodeIds);
        Assert.Equal(["x@0xN"], Queued());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
