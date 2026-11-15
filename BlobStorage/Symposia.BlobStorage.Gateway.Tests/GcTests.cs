using System.Net;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.GC;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Garbage collection (issue #16).</summary>
[Collection("GatewayIntegration")]
public sealed class GcTests
{
    private static GcWorker Worker(TestCluster cluster) => new(
        cluster.Services.GetRequiredService<GatewayMetadataStore>(), cluster.Registry,
        Options.Create(new GatewayOptions { GcMinAgeSeconds = 0 }), NullLogger<GcWorker>.Instance,
        cluster.Services.GetRequiredService<SafetyInvariantGate>());

    private static GcEvaluator Evaluator(GatewayMetadataStore store, int recoveryDays = 30) =>
        new(store, Options.Create(new GatewayOptions { Gc = { RecoveryDays = recoveryDays } }), NullLogger<GcEvaluator>.Instance);

    private static async Task<bool> NodeHasAsync(NodeConnection node, string cid)
    {
        try
        {
            await node.Client.IntegrityChallengeAsync(new IntegrityChallengeRequest { Cid = cid });
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }

    // ── Soft delete purge (FR2, AC3, AC13) ────────────────────────────────────

    [Fact]
    public async Task SoftDeleted_IsPurgedFromEveryReplicaAfterTheWindow_WithConfirmationEventAndBillingStop()
    {
        using var cluster = await TestCluster.StartAsync(2);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var cid = await cluster.PutAsync("gc/purge", "purge me "u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        var usage = store.TenantUsageBytes("test-tenant");

        await cluster.S3.DeleteObjectAsync(TestCluster.Bucket, "gc/purge");
        Assert.Equal(usage, store.TenantUsageBytes("test-tenant")); // still billed
        Evaluator(store).Evaluate(DateTimeOffset.UtcNow.AddDays(31));
        Assert.Equal(2, await Worker(cluster).DrainBatchAsync(CancellationToken.None));

        foreach (var node in cluster.Connections)
        {
            Assert.False(await NodeHasAsync(node, cid));
        }

        var version = store.Versions(TestCluster.Bucket, "gc/purge").Single();
        Assert.NotNull(version.PhysicallyDeletedAt); // the record stays for audit (FR2.3)
        Assert.Contains(store.Events("blob.physically_deleted"), e => e.Cid == cid);
        Assert.True(store.TenantUsageBytes("test-tenant") < usage); // billing stops at physical deletion
    }

    [Fact]
    public async Task DeletedObject_CanBeRestoredWithinTheWindow()
    {
        using var cluster = await TestCluster.StartAsync(1);
        await cluster.PutAsync("gc/restore", "come back"u8.ToArray());
        await cluster.S3.DeleteObjectAsync(TestCluster.Bucket, "gc/restore");

        using var http = cluster.Signed();
        var restore = await http.PostAsync($"/v1/buckets/{TestCluster.Bucket}/objects/gc/restore?restore", null);

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal("come back"u8.ToArray(), await cluster.GetAsync("gc/restore"));
    }

    // ── Locks and legal holds (FR2.4, FR7.2, AC4) ─────────────────────────────

    [Fact]
    public async Task HeldObject_IsNeverPurged_EvenPastItsWindow_AndTheGateRefusesADirectAttempt()
    {
        using var cluster = await TestCluster.StartAsync(1);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var cid = await cluster.PutAsync("gc/held", "evidence"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        Assert.True(store.SetRetention(TestCluster.Bucket, "gc/held", null, legalHold: true));
        await cluster.S3.DeleteObjectAsync(TestCluster.Bucket, "gc/held");

        Evaluator(store).Evaluate(DateTimeOffset.UtcNow.AddDays(400));
        Assert.DoesNotContain(store.DueDeletions(100, TimeSpan.Zero, 0), e => e.Cid == cid);

        // Even if something queued it anyway, the gate refuses.
        store.EnqueueNodeDeletion(cid, cluster.Connections[0].Id, "SoftDeletePurge");
        await Worker(cluster).DrainBatchAsync(CancellationToken.None);
        Assert.True(await NodeHasAsync(cluster.Connections[0], cid));
        Assert.True(cluster.Services.GetRequiredService<SafetyInvariantGate>().Refusals > 0);
    }

    // ── Versions (FR3, AC5, AC6) ──────────────────────────────────────────────

    [Fact]
    public async Task VersionExpiry_NeverBeatsMinimumRetention_AndOrphanedMarkersGo()
    {
        using var cluster = await TestCluster.StartAsync(1);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        store.SetLifecycle(TestCluster.Bucket, new BucketLifecycle(Versioning: true, MaxVersions: 1, MinRetentionDays: 10));
        for (var i = 0; i < 3; i++)
        {
            await cluster.PutAsync("gc/versions", [(byte)i, .. Guid.NewGuid().ToByteArray()]);
        }

        Assert.Equal(2, store.Versions(TestCluster.Bucket, "gc/versions").Count(v => v.Kind == "noncurrent"));
        var evaluator = Evaluator(store);
        Assert.Equal(0, evaluator.Evaluate(DateTimeOffset.UtcNow).Expired);                // aggressive rule, retention wins
        Assert.Equal(1, evaluator.Evaluate(DateTimeOffset.UtcNow.AddDays(11)).Expired);   // past retention: beyond max 1

        await cluster.S3.DeleteObjectAsync(TestCluster.Bucket, "gc/versions");
        Assert.Contains(store.Versions(TestCluster.Bucket, "gc/versions"), v => v.Kind == "marker");
        foreach (var v in store.Versions(TestCluster.Bucket, "gc/versions").Where(v => v.Kind == "noncurrent" && v.DeletedAt is null))
        {
            store.SoftDeleteVersion(v.Id);
        }

        evaluator.Evaluate(DateTimeOffset.UtcNow);
        Assert.DoesNotContain(store.Versions(TestCluster.Bucket, "gc/versions"), v => v.Kind == "marker");
    }

    // ── Multipart (FR1, AC1, AC2) ─────────────────────────────────────────────

    [Fact]
    public void MultipartTtl_IsClamped_AndOnlyGcAbortsEmitTheEvent()
    {
        var o = new GatewayOptions();
        Assert.Equal(TimeSpan.FromDays(7), o.MultipartTtlFor(null));
        Assert.Equal(TimeSpan.FromHours(1), o.MultipartTtlFor(0));
        Assert.Equal(TimeSpan.FromDays(30), o.MultipartTtlFor(10_000));

        var store = new GatewayMetadataStore(Options.Create(new GatewayOptions
        {
            MetadataDbPath = Path.Combine(Path.GetTempPath(), $"symposia-gc-{Guid.NewGuid():N}", "m.db"),
        }));
        store.Initialize();
        var now = DateTimeOffset.UtcNow;
        store.CreateUpload(new MultipartUpload("stale", "t", "b", "k", "x", null, null, now.AddHours(-3), now.AddHours(-1), MultipartStatus.InProgress, null, null));
        store.CreateUpload(new MultipartUpload("manual", "t", "b", "k2", "x", null, null, now, now.AddHours(1), MultipartStatus.InProgress, null, null));

        store.AbortUpload("manual", TimeSpan.FromHours(1));
        store.ReapUploads(now, TimeSpan.FromHours(1), TimeSpan.FromDays(7), emitEvents: true);

        Assert.Equal([("b", "k")], store.Events("blob.multipart_aborted").Select(e => (e.Bucket!, e.Key!)));
    }

    // ── Orphans (FR5, AC9) ────────────────────────────────────────────────────

    private sealed class ConfirmingOracle : IOnChainMetadataOracle
    {
        public Task<bool> ConfirmsNoRecordAsync(string cid, CancellationToken ct) => Task.FromResult(true);
    }

    [Fact]
    public async Task Orphans_WaitOutTheGrace_AndNeedOnChainConfirmation()
    {
        using var cluster = await TestCluster.StartAsync(1);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var node = cluster.Connections[0];
        string orphan;
        using (var write = node.Client.WriteBlob())
        {
            await write.RequestStream.WriteAsync(new WriteBlobChunk { Header = new WriteBlobHeader() });
            await write.RequestStream.WriteAsync(new WriteBlobChunk { Data = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()) });
            await write.RequestStream.CompleteAsync();
            orphan = (await write.ResponseAsync).Cid;
        }

        NodeReconciler Reconciler(IOnChainMetadataOracle oracle, int graceHours) => new(store, new Repair.RepairQueue(), cluster.Registry,
            NullLogger<NodeReconciler>.Instance, oracle, Options.Create(new GatewayOptions { Gc = { OrphanGraceHours = graceHours } }));

        await Reconciler(new ConfirmingOracle(), 48).ReconcileAsync(node, CancellationToken.None);
        Assert.DoesNotContain(store.DueDeletions(100, TimeSpan.Zero, 0), e => e.Cid == orphan); // too young

        await Reconciler(new FailClosedOracle(), 0).ReconcileAsync(node, CancellationToken.None);
        Assert.DoesNotContain(store.DueDeletions(100, TimeSpan.Zero, 0), e => e.Cid == orphan); // chain can't confirm

        await Reconciler(new ConfirmingOracle(), 0).ReconcileAsync(node, CancellationToken.None);
        var entry = Assert.Single(store.DueDeletions(100, TimeSpan.Zero, 0), e => e.Cid == orphan);
        Assert.Equal(GatewayMetadataStore.PriorityLow, entry.Priority);
    }

    // ── Safety and priority (FR7, FR8, AC10, AC12) ────────────────────────────

    [Fact]
    public async Task Gate_RefusesAnyCategoryQueuedForReadableData()
    {
        using var cluster = await TestCluster.StartAsync(1);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var cid = await cluster.PutAsync("gc/live", "live"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

        store.EnqueueNodeDeletion(cid, cluster.Connections[0].Id, "OrphanReconcile", GatewayMetadataStore.PriorityLow);
        await Worker(cluster).DrainBatchAsync(CancellationToken.None);

        Assert.True(await NodeHasAsync(cluster.Connections[0], cid));
        Assert.Equal("live"u8.ToArray().Length + 16, (await cluster.GetAsync("gc/live")).Length);
        Assert.True(cluster.Services.GetRequiredService<SafetyInvariantGate>().Refusals >= 1);
    }

    [Fact]
    public void Queue_HighBeforeLow_WithALowReserve()
    {
        var store = new GatewayMetadataStore(Options.Create(new GatewayOptions
        {
            MetadataDbPath = Path.Combine(Path.GetTempPath(), $"symposia-gc-{Guid.NewGuid():N}", "m.db"),
        }));
        store.Initialize();
        for (var i = 0; i < 20; i++) store.EnqueueNodeDeletion($"high{i}", "n", "SlashedNodePurge", GatewayMetadataStore.PriorityHigh);
        for (var i = 0; i < 20; i++) store.EnqueueNodeDeletion($"low{i}", "n", "OrphanReconcile", GatewayMetadataStore.PriorityLow);

        var batch = store.DueDeletions(10, TimeSpan.Zero, reserveLow: 2);

        Assert.Equal(8, batch.Count(e => e.Priority == GatewayMetadataStore.PriorityHigh));
        Assert.Equal(2, batch.Count(e => e.Priority == GatewayMetadataStore.PriorityLow)); // deferred, not starved
    }

    // ── Stale replicas (FR4, AC7, AC8) ────────────────────────────────────────

    [Fact]
    public async Task UnreplicableOfflineReference_StaysFlagged_AsAPlacementWarning()
    {
        using var cluster = await TestCluster.StartAsync(1);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var cid = await cluster.PutAsync("gc/offline", "lonely"u8.ToArray());
        store.MarkOffline(TestCluster.Bucket, "gc/offline", cid, "http://gone");
        store.FlagPlacementWarning(cid, "no eligible node");

        using var http = cluster.Signed();
        var report = await http.GetStringAsync("/v1/gc");

        Assert.Contains("no eligible node", report);
        Assert.Contains("gc/offline", report);
        Assert.Equal(["http://gone"], store.OfflineReplicas(TestCluster.Bucket, "gc/offline"));
    }
}
