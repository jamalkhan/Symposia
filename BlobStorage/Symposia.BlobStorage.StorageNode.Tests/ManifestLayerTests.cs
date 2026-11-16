using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.StorageNode.Storage;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>
/// #12 Layer 1: the manifest's durability, references, change-feed outbox, epoch freezing and rebuild from disk,
/// and the shared epoch Merkle tree (QA TC1.1–TC1.7, TC3.2, TC3.6).
/// </summary>
public sealed class ManifestLayerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym12-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "manifest.db");

    private ManifestStore Store(long retention = 100_000)
    {
        var store = new ManifestStore(Options.Create(new StorageNodeOptions { ManifestDbPath = DbPath, ManifestEventRetention = retention }));
        store.Initialize();
        return store;
    }

    private static Cid CidOf(string content) => Cid.FromHash(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static BlobRecord Record(Cid cid, long size, string tenant = "t1", string bucket = "b", string key = "k", BlobStatus status = BlobStatus.Active) =>
        new(cid, size, tenant, bucket, key, [], DateTimeOffset.UtcNow, null, status);

    [Fact]
    public void FreshNode_StartsEmpty_AndManifestSurvivesARestart()
    {
        var store = Store();
        Assert.True(store.NeedsRebuild); // no database yet: a rebuild that finds nothing
        var cid = CidOf("durable");
        store.Upsert(Record(cid, 7, key: "a.txt"));

        var reopened = Store();

        Assert.False(reopened.NeedsRebuild);
        var record = reopened.Get(cid);
        Assert.NotNull(record);
        Assert.Equal(("t1", "b", "a.txt", BlobStatus.Active), (record.TenantId, record.Bucket, record.Key, record.Status));
        Assert.Equal(7, reopened.UsedBytes);
    }

    [Fact]
    public void CorruptDatabase_IsQuarantinedAndFlaggedForRebuild()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(DbPath, Encoding.ASCII.GetBytes("this is not a sqlite database, just bytes on disk"));

        var store = Store();

        Assert.True(store.NeedsRebuild);
        Assert.NotNull(store.QuarantinedPath);
        Assert.True(File.Exists(store.QuarantinedPath)); // moved aside, never deleted
        Assert.Equal(0, store.CountBlobs());
    }

    [Fact]
    public void ByteIdenticalBlobsFromTwoTenants_KeepBothReferences_AndRepairCopiesAddNone()
    {
        var store = Store();
        var cid = CidOf("shared bytes");
        store.Upsert(Record(cid, 12, tenant: "alice", bucket: "a", key: "x"));
        store.Upsert(Record(cid, 12, tenant: "bob", bucket: "b", key: "y"));
        store.Upsert(Record(cid, 12, tenant: ManifestStore.RepairTenant, bucket: "", key: ""));

        Assert.Equal([("alice", "a", "x"), ("bob", "b", "y")], store.References(cid));
        Assert.Equal("bob", store.Get(cid)!.TenantId); // the legacy columns keep the last real writer, not "repair"
        Assert.Equal(12, store.UsedBytes); // stored once
    }

    [Fact]
    public void EveryChange_IsRecordedInTheOutbox_InOrder()
    {
        var store = Store();
        var a = CidOf("a");
        var b = CidOf("b");
        store.Upsert(Record(a, 1));
        store.Upsert(Record(b, 2));
        store.SetStatus(a, BlobStatus.PendingGc);
        store.SetStatus(a, BlobStatus.PendingGc); // no change, no event
        store.UpdateChecksumVerifiedAt(b, DateTimeOffset.UtcNow); // not a holding change
        store.Delete(b);

        var events = store.EventsAfter(0, 100);

        Assert.Equal([(a, false, BlobStatus.Active), (b, false, BlobStatus.Active), (a, false, BlobStatus.PendingGc), (b, true, BlobStatus.Orphaned)],
            events.Select(e => (e.Cid, e.Removed, e.Status)));
        Assert.Equal(events[^1].Seq, store.HeadSeq());
        Assert.Equal([events[^1]], store.EventsAfter(events[^2].Seq, 100));
        Assert.Equal(0, store.CountActive());
    }

    [Fact]
    public void OutboxIsPrunedToItsRetention()
    {
        var store = Store(retention: 10);
        for (var i = 0; i < 1001; i++)
        {
            store.Upsert(Record(CidOf("blob" + i), 1));
        }

        Assert.True(store.OldestSeq() >= store.HeadSeq() - 10);
        Assert.Equal(1001, store.HeadSeq());
    }

    [Fact]
    public void FreezeEpoch_AttestsActiveHoldingsPerTenant_AndIsStable()
    {
        var store = Store();
        var shared = CidOf("shared");
        var orphan = CidOf("orphan");
        var unreferenced = CidOf("rebuilt");
        store.Upsert(Record(shared, 5, tenant: "alice"));
        store.Upsert(Record(shared, 5, tenant: "bob", key: "other"));
        store.Upsert(Record(shared, 5, tenant: "bob", key: "third")); // a second key, same tenant: still one leaf
        store.Upsert(Record(orphan, 3, status: BlobStatus.Orphaned));
        store.Upsert(Record(unreferenced, 9, tenant: ""));

        var frozen = store.FreezeEpoch(4);
        store.Upsert(Record(CidOf("later"), 1)); // after the boundary: not in epoch 4
        var again = store.FreezeEpoch(4);

        var expected = new[] { new ManifestLeaf(shared, 5, "alice"), new ManifestLeaf(shared, 5, "bob"), new ManifestLeaf(unreferenced, 9, "") };
        Assert.Equal(ManifestMerkle.Sort(expected), store.EpochLeaves(4));
        Assert.Equal(ManifestMerkle.Root(expected), frozen.Root);
        Assert.Equal(frozen.Root, again.Root);
        Assert.Equal(3, frozen.LeafCount);

        store.MarkEpoch(4, "submitted", onChain: true);
        Assert.Equal(4UL, store.LatestSubmittedEpoch()!.Epoch);
        store.PruneEpochs(5);
        Assert.Null(store.GetEpochRoot(4));
    }

    [Fact]
    public void Rebuild_RestoresOnlyVerifiedBlobs_AsActiveAdditions()
    {
        var blobs = new LocalBlobStore(Options.Create(new StorageNodeOptions { StorageRoot = Path.Combine(_dir, "blobs") }));
        blobs.EnsureRootExists();
        var good = blobs.WriteAsync(new MemoryStream("acknowledged or not, it's on disk"u8.ToArray()), CancellationToken.None).GetAwaiter().GetResult().Cid;
        var bad = CidOf("what this file should contain");
        var badPath = Path.Combine(_dir, "blobs", bad.ToShardedRelativePath());
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        File.WriteAllText(badPath, "bit rot");

        var store = Store(); // no database: the crash took it
        var result = ManifestRecovery.RebuildIfNeeded(store, blobs, NullLogger.Instance);

        Assert.Equal(new ManifestRecovery.Result(1, 1), result);
        Assert.Equal(BlobStatus.Active, store.Get(good)!.Status);
        Assert.Null(store.Get(bad)); // never fabricated
        Assert.True(File.Exists(badPath)); // and never deleted
        Assert.Equal([good], store.EventsAfter(0, 10).Select(e => e.Cid)); // gossiped as a (re-)addition
        Assert.Equal(new ManifestRecovery.Result(0, 0), ManifestRecovery.RebuildIfNeeded(Store(), blobs, NullLogger.Instance));
    }

    // ── ManifestMerkle (the shared reference implementation) ─────────────────

    [Fact]
    public void ManifestRoot_MatchesAnIndependentRecomputation_AndIgnoresInputOrder()
    {
        var a = new ManifestLeaf(CidOf("one"), 10, "alice");
        var b = new ManifestLeaf(CidOf("two"), 20, "bob");

        static byte[] Leaf(ManifestLeaf l)
        {
            var data = new byte[40 + Encoding.UTF8.GetByteCount(l.TenantId)];
            Convert.FromHexString(l.Cid.Value).CopyTo(data, 0);
            BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(32), l.SizeBytes);
            Encoding.UTF8.GetBytes(l.TenantId).CopyTo(data, 40);
            return SHA256.HashData([0x00, .. data]);
        }

        var (first, second) = string.CompareOrdinal(a.Cid.Value, b.Cid.Value) < 0 ? (a, b) : (b, a);
        var expected = SHA256.HashData([0x01, .. Leaf(first), .. Leaf(second)]);

        Assert.Equal(expected, ManifestMerkle.Root([a, b]));
        Assert.Equal(expected, ManifestMerkle.Root([b, a]));
        Assert.NotEqual(new byte[32], ManifestMerkle.Root([])); // never the registry's "no submission" sentinel
    }

    [Fact]
    public void InclusionProofs_VerifyForMembers_AndFailForAnythingElse()
    {
        var leaves = ManifestMerkle.Sort(Enumerable.Range(0, 7).Select(i => new ManifestLeaf(CidOf("blob" + i), i * 100, i % 2 == 0 ? "alice" : "bob")));
        var hashes = ManifestMerkle.LeafHashes(leaves);
        var root = MerkleTree.Root(hashes);

        for (var i = 0; i < leaves.Count; i++)
        {
            var proof = MerkleTree.Proof(hashes, i);
            Assert.True(ManifestMerkle.Verify(leaves[i], i, leaves.Count, proof, root));
            Assert.False(ManifestMerkle.Verify(leaves[i] with { SizeBytes = leaves[i].SizeBytes + 1 }, i, leaves.Count, proof, root));
            Assert.False(ManifestMerkle.Verify(leaves[i] with { TenantId = "mallory" }, i, leaves.Count, proof, root));
        }

        // True negative (TC3.6): a CID that isn't in the tree can't borrow another leaf's proof.
        var absent = new ManifestLeaf(CidOf("never stored"), 0, "alice");
        Assert.False(ManifestMerkle.Verify(absent, 0, leaves.Count, MerkleTree.Proof(hashes, 0), root));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
