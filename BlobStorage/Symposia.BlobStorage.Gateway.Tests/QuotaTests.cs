using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Quota;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// #15 quota management: 507 rejections with details on both APIs, account self-limits, bucket byte and object-count
/// quotas, race-safe reservations, release on failure and on lapse, multipart and copy, threshold alerts, and the free
/// tier's block-then-unblock-on-payment flow (QA-01–QA-48 where they apply to one gateway).
/// </summary>
public sealed class QuotaTests
{
    private const string Tenant = "test-tenant";
    private const string Admin = "admin-secret";

    private sealed class RecordingAlerts : IQuotaAlertSink
    {
        public List<QuotaAlert> Alerts { get; } = [];

        public void Notify(QuotaAlert alert)
        {
            lock (Alerts) Alerts.Add(alert);
        }
    }

    private static Task<TestCluster> StartAsync(long freeTier = 0, RecordingAlerts? alerts = null, int nodes = 2,
        Dictionary<string, string?>? extra = null, Func<int, DelegatingHandler?>? handler = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Gateway:Quota:FreeTierStorageBytes"] = freeTier.ToString(),
            ["Gateway:AdminSecret"] = Admin,
            ["Gateway:MultipartMinPartSizeBytes"] = "1",
        };
        foreach (var (k, v) in extra ?? []) config[k] = v;
        return TestCluster.StartAsync(nodes, config, handler: handler,
            services: alerts is null ? null : s => s.Replace(ServiceDescriptor.Singleton<IQuotaAlertSink>(alerts)));
    }

    private static Task<HttpResponseMessage> PutV1Async(TestCluster cluster, string key, int size) =>
        cluster.Signed().PutAsync($"/v1/buckets/{TestCluster.Bucket}/objects/{key}", new ByteArrayContent(new byte[size]));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static GatewayMetadataStore Store(TestCluster cluster) => cluster.Services.GetRequiredService<GatewayMetadataStore>();

    // ── 507 and its details (AC1, AC2, AC5) ───────────────────────────────────

    [Fact]
    public async Task BucketQuota_Rejects507WithDetails_OnBothApis_AndUsageIsImmediate()
    {
        using var cluster = await StartAsync();
        var signed = cluster.Signed();
        Assert.Equal(HttpStatusCode.OK, (await signed.PutAsJsonAsync($"/v1/buckets/{TestCluster.Bucket}/quota", new { limit_bytes = 100 })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "a", 60)).StatusCode);
        var quota = await JsonAsync(await signed.GetAsync($"/v1/buckets/{TestCluster.Bucket}/quota"));
        Assert.Equal(60, quota.GetProperty("used_bytes").GetInt64()); // logical size once, though 2 nodes hold it (AC5)
        Assert.Equal(40, quota.GetProperty("available_bytes").GetInt64());
        Assert.Equal(60.0, quota.GetProperty("utilization_pct").GetDouble());

        var native = await PutV1Async(cluster, "b", 41);
        Assert.Equal((HttpStatusCode)507, native.StatusCode);
        var error = (await JsonAsync(native)).GetProperty("error");
        Assert.Equal("BucketQuotaExceeded", error.GetProperty("code").GetString());
        var details = error.GetProperty("details");
        Assert.Equal(("bucket", 100, 60, 41, 1),
            (details.GetProperty("quota_type").GetString(), details.GetProperty("limit_bytes").GetInt64(), details.GetProperty("used_bytes").GetInt64(),
             details.GetProperty("incoming_bytes").GetInt64(), details.GetProperty("exceeded_by_bytes").GetInt64()));

        var s3 = await cluster.Signed().PutAsync($"/{TestCluster.Bucket}/c", new ByteArrayContent(new byte[41]));
        Assert.Equal((HttpStatusCode)507, s3.StatusCode);
        var xml = XDocument.Parse(await s3.Content.ReadAsStringAsync()).Root!;
        Assert.Equal("QuotaExceeded", xml.Element("Code")!.Value);
        Assert.Equal("bucket", xml.Element("QuotaType")!.Value);
        Assert.Equal("41", xml.Element("IncomingBytes")!.Value);

        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "exactly-full", 40)).StatusCode); // landing exactly on the limit fits (QA-02)
        Assert.Equal((HttpStatusCode)507, (await PutV1Async(cluster, "empty", 0)).StatusCode); // a full quota takes nothing more

        Assert.Equal(HttpStatusCode.NoContent, (await signed.DeleteAsync($"/v1/buckets/{TestCluster.Bucket}/quota")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "unbounded", 41)).StatusCode); // only the (unlimited) account applies now
    }

    [Fact]
    public async Task SelfLimit_IsTheEffectiveLimitWhenLower_AndRemovingItRevertsToThePlatformLimit()
    {
        using var cluster = await StartAsync(extra: new() { [$"Gateway:TenantQuotaBytes:{Tenant}"] = "1000" });
        var signed = cluster.Signed();

        var set = await JsonAsync(await signed.PutAsJsonAsync("/v1/account/quota", new { tenant_self_limit_bytes = 50 }));
        Assert.Equal((1000, 50, 50), (set.GetProperty("platform_imposed_limit_bytes").GetInt64(), set.GetProperty("tenant_self_limit_bytes").GetInt64(),
            set.GetProperty("effective_limit_bytes").GetInt64()));
        var refused = await PutV1Async(cluster, "big", 51);
        Assert.Equal((HttpStatusCode)507, refused.StatusCode);
        Assert.Equal("AccountQuotaExceeded", (await JsonAsync(refused)).GetProperty("error").GetProperty("code").GetString());

        var above = await JsonAsync(await signed.PutAsJsonAsync("/v1/account/quota", new { tenant_self_limit_bytes = 5000 })); // accepted, capped (QA-30)
        Assert.Equal(1000, above.GetProperty("effective_limit_bytes").GetInt64());

        var removed = await JsonAsync(await signed.DeleteAsync("/v1/account/quota/self-limit"));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("tenant_self_limit_bytes").ValueKind);
        Assert.Equal(1000, removed.GetProperty("effective_limit_bytes").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "big", 51)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await signed.PutAsJsonAsync("/v1/account/quota", new { tenant_self_limit_bytes = -1 })).StatusCode);
    }

    [Fact]
    public async Task BucketQuotaApi_IsScopedToTheBucketsOwner()
    {
        using var cluster = await StartAsync(extra: new()
        {
            ["Gateway:Credentials:1:AccessKeyId"] = "AKIAOTHERTENANT00001",
            ["Gateway:Credentials:1:SecretAccessKey"] = "other-secret",
            ["Gateway:Credentials:1:TenantId"] = "other-tenant",
        });
        var stranger = cluster.Signed("AKIAOTHERTENANT00001", "other-secret");

        var read = await stranger.GetAsync($"/v1/buckets/{TestCluster.Bucket}/quota");
        var write = await stranger.PutAsJsonAsync($"/v1/buckets/{TestCluster.Bucket}/quota", new { limit_bytes = 1 });

        Assert.Contains(read.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden }); // QA-34
        Assert.Contains(write.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });
        Assert.Null(Store(cluster).GetBucketQuota(TestCluster.Bucket));
    }

    [Fact]
    public async Task ARecreatedBucket_StartsWithoutTheOldQuotaOrUsage()
    {
        using var cluster = await StartAsync();
        var signed = cluster.Signed();
        await signed.PutAsync("/v1/buckets/recycled", null);
        await signed.PutAsJsonAsync("/v1/buckets/recycled/quota", new { limit_bytes = 100 });
        await signed.PutAsync("/v1/buckets/recycled/objects/x", new ByteArrayContent(new byte[60]));
        await signed.DeleteAsync("/v1/buckets/recycled/objects/x");
        Store(cluster).DeleteBucket("recycled"); // the soft-deleted version's bytes are still billed to the old owner

        await signed.PutAsync("/v1/buckets/recycled", null);

        Assert.Null(Store(cluster).GetBucketQuota("recycled"));
        Assert.Equal(0, Store(cluster).BucketUsage("recycled").UsedBytes);
    }

    [Fact]
    public async Task Overwrites_NeedRoomOnlyForTheirGrowth()
    {
        using var cluster = await StartAsync();
        Store(cluster).SetBucketQuota(TestCluster.Bucket, 100, null);
        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "backup", 90)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "backup", 95)).StatusCode); // replaces 90 bytes: needs 5 more
        Assert.Equal(95, Store(cluster).BucketUsage(TestCluster.Bucket).UsedBytes);
        Assert.Equal((HttpStatusCode)507, (await PutV1Async(cluster, "backup", 101)).StatusCode);
    }

    [Fact]
    public void Upgrading_KeepsExistingTenantsUnlimited_WhileNewOnesGetTheFreeTier()
    {
        var path = Path.Combine(Path.GetTempPath(), $"symposia-quota-upgrade-{Guid.NewGuid():N}", "m.db");
        var store = new GatewayMetadataStore(Microsoft.Extensions.Options.Options.Create(new GatewayOptions { MetadataDbPath = path }));
        store.Initialize();
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            // What a pre-#15 database looks like: usage, no quota tables.
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE bucket_usage; DELETE FROM account_quota; INSERT INTO tenant_usage (tenant_id, bytes) VALUES ('veteran', 50000000000);";
            cmd.ExecuteNonQuery();
        }

        store.Initialize();

        var freeTier = new AccountDefaults(10L << 30, null, FreeTier: true);
        var veteran = store.Account("veteran", freeTier);
        Assert.Equal((null, false), (veteran.PlatformLimitBytes, veteran.FreeTier));
        Assert.Equal((10L << 30, true), (store.Account("newcomer", freeTier).PlatformLimitBytes, store.Account("newcomer", freeTier).FreeTier));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    // ── Reservations (AC3, AC4) ───────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentWrites_NeverJointlyOverrunTheLimit()
    {
        using var cluster = await StartAsync();
        Store(cluster).SetBucketQuota(TestCluster.Bucket, 100, null);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => PutV1Async(cluster, $"race-{i}", 30)));

        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => (int)r.StatusCode == 507));
        Assert.Equal(90, Store(cluster).BucketUsage(TestCluster.Bucket).UsedBytes);
        Assert.Equal(0, Store(cluster).BucketUsage(TestCluster.Bucket).ReservedBytes);
    }

    [Fact]
    public async Task AFailedWrite_ReleasesItsReservation_AndALapsedLeaseIsSwept()
    {
        var down = new BlackHoleHandler { Enabled = false };
        using var cluster = await StartAsync(nodes: 1, handler: _ => down);
        var store = Store(cluster);
        store.SetBucketQuota(TestCluster.Bucket, 100, null);

        down.Enabled = true;
        Assert.NotEqual(HttpStatusCode.OK, (await PutV1Async(cluster, "lost", 100)).StatusCode); // no quorum
        Assert.Equal(new QuotaUsage(0, 0, 0, 0), store.BucketUsage(TestCluster.Bucket));
        down.Enabled = false;
        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "retried", 100)).StatusCode); // the headroom came back

        // A gateway that died mid-upload never ends its reservation; its lease lapses and the sweep returns it.
        var quota = cluster.Services.GetRequiredService<QuotaGate>();
        store.SetBucketQuota(TestCluster.Bucket, 200, null);
        var (id, refusal) = store.TryReserve(Tenant, quota.DefaultsFor(Tenant), quota.EffectiveLimit, TestCluster.Bucket, "hung", 100, TimeSpan.Zero);
        Assert.Null(refusal);
        Assert.Equal(1, store.SweepReservations());
        Assert.Equal(0, store.BucketUsage(TestCluster.Bucket).ReservedBytes);
        store.EndReservation(id!.Value); // ending an already-swept reservation is harmless
    }

    // ── Object count (AC9) ────────────────────────────────────────────────────

    [Fact]
    public async Task ObjectCountQuota_IsIndependentOfBytes_AndOverwritesDontCount()
    {
        using var cluster = await StartAsync();
        var signed = cluster.Signed();
        await signed.PutAsJsonAsync($"/v1/buckets/{TestCluster.Bucket}/quota", new { max_object_count = 2 });
        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "one", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "two", 1)).StatusCode);

        var third = await PutV1Async(cluster, "three", 1);
        Assert.Equal((HttpStatusCode)507, third.StatusCode);
        Assert.Equal(2, (await JsonAsync(third)).GetProperty("error").GetProperty("details").GetProperty("limit_objects").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "two", 5)).StatusCode); // replacing an object adds none

        await signed.DeleteAsync($"/v1/buckets/{TestCluster.Bucket}/objects/one");
        Assert.Equal(1, Store(cluster).BucketUsage(TestCluster.Bucket).Objects);
        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "three", 1)).StatusCode);
    }

    // ── Multipart and copy (AC6, server-side copy) ────────────────────────────

    [Fact]
    public async Task MultipartParts_DontCount_AndCompletionIsCheckedForTheWholeObject()
    {
        using var cluster = await StartAsync();
        var store = Store(cluster);
        store.SetBucketQuota(TestCluster.Bucket, 100, null);
        var upload = (await cluster.S3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = TestCluster.Bucket, Key = "mp" })).UploadId;
        var etags = new List<PartETag>();
        for (var part = 1; part <= 3; part++)
        {
            var r = await cluster.S3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = TestCluster.Bucket, Key = "mp", UploadId = upload, PartNumber = part, InputStream = new MemoryStream(new byte[40]),
                UseChunkEncoding = false,
            });
            etags.Add(new PartETag(part, r.ETag));
        }

        Assert.Equal(0, store.BucketUsage(TestCluster.Bucket).UsedBytes); // 120 bytes of parts, none counted

        var tooBig = await Assert.ThrowsAsync<AmazonS3Exception>(() => cluster.S3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = TestCluster.Bucket, Key = "mp", UploadId = upload, PartETags = etags,
        }));
        Assert.Equal((HttpStatusCode)507, tooBig.StatusCode);

        await cluster.S3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = TestCluster.Bucket, Key = "mp", UploadId = upload, PartETags = etags.Take(2).ToList(),
        });
        Assert.Equal(80, store.BucketUsage(TestCluster.Bucket).UsedBytes); // the parts left the upload in progress; it completes within quota
    }

    [Fact]
    public async Task Copy_IsChargedAtTheDestination()
    {
        using var cluster = await StartAsync();
        await cluster.PutAsync("source", new byte[60]);
        Store(cluster).SetBucketQuota(TestCluster.Bucket, 100, null);

        var copy = await Assert.ThrowsAsync<AmazonS3Exception>(() => cluster.S3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = TestCluster.Bucket, SourceKey = "source", DestinationBucket = TestCluster.Bucket, DestinationKey = "copy",
        }));
        Assert.Equal((HttpStatusCode)507, copy.StatusCode);
    }

    // ── Alerts (AC10) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Thresholds_AlertOncePerCrossing_AndRearmBelow()
    {
        var alerts = new RecordingAlerts();
        using var cluster = await StartAsync(alerts: alerts, extra: new() { [$"Gateway:TenantQuotaBytes:{Tenant}"] = "100" });

        await PutV1Async(cluster, "a", 76); // 76 %: INFO
        await PutV1Async(cluster, "b", 5); // 81 %: nothing new
        await PutV1Async(cluster, "c", 10); // 91 %: WARNING
        await PutV1Async(cluster, "d", 9); // 100 %: CRITICAL
        Assert.Equal((HttpStatusCode)507, (await PutV1Async(cluster, "e", 1)).StatusCode);

        Assert.Equal([(75, QuotaAlertSeverity.Info), (90, QuotaAlertSeverity.Warning), (100, QuotaAlertSeverity.Critical)],
            alerts.Alerts.Select(a => (a.ThresholdPct, a.Severity)));
        Assert.All(alerts.Alerts, a => Assert.Equal(("account", Tenant), (a.Scope, a.TenantId)));
        Assert.Equal(76.0, alerts.Alerts[0].UtilizationPct);

        // Back below every threshold, then up again: each crossing alerts again (QA-41).
        Assert.Empty(Store(cluster).CrossThresholds("account", Tenant, 50, [75, 90, 100]));
        Assert.Equal([75, 90], Store(cluster).CrossThresholds("account", Tenant, 95, [75, 90, 100]));
    }

    // ── Free tier (AC11, AC12) ────────────────────────────────────────────────

    [Fact]
    public async Task NewAccounts_StartOnTheFreeTier_AndPaymentLiftsTheLimitWithoutOperatorAction()
    {
        using var cluster = await StartAsync(freeTier: 100);
        var signed = cluster.Signed();

        var fresh = await JsonAsync(await signed.GetAsync("/v1/account/quota"));
        Assert.Equal(100, fresh.GetProperty("platform_imposed_limit_bytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, fresh.GetProperty("tenant_self_limit_bytes").ValueKind);
        Assert.Equal(100L << 30, fresh.GetProperty("egress_limit_bytes_per_month").GetInt64());
        Assert.True(fresh.GetProperty("free_tier").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "fill", 100)).StatusCode);
        var blocked = await PutV1Async(cluster, "more", 10);
        Assert.Equal((HttpStatusCode)507, blocked.StatusCode);
        var error = (await JsonAsync(blocked)).GetProperty("error");
        Assert.True(error.GetProperty("details").GetProperty("payment_required").GetBoolean());
        Assert.Contains("payment", error.GetProperty("message").GetString());

        // What billing calls once payment credentials are on file with a 500-byte contract.
        using var admin = new HttpRequestMessage(HttpMethod.Put, $"/admin/tenants/{Tenant}/quota")
        {
            Content = JsonContent.Create(new { platform_imposed_limit_bytes = 500 }),
        };
        admin.Headers.Add("X-Admin-Secret", Admin);
        var upgraded = await JsonAsync(await cluster.Http.SendAsync(admin));
        Assert.Equal(500, upgraded.GetProperty("effective_limit_bytes").GetInt64());
        Assert.False(upgraded.GetProperty("free_tier").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "more", 10)).StatusCode); // the rejected write, retried
    }

    [Fact]
    public async Task NoQuotaAnywhere_IsUnlimited()
    {
        using var cluster = await StartAsync(freeTier: 0);
        var quota = await JsonAsync(await cluster.Signed().GetAsync("/v1/account/quota"));
        Assert.Equal(JsonValueKind.Null, quota.GetProperty("limit_bytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, quota.GetProperty("available_bytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, quota.GetProperty("utilization_pct").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await PutV1Async(cluster, "anything", 1000)).StatusCode);
    }
}
