using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Api;
using Symposia.BlobStorage.Gateway.History;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Per-object operation history (issue #204).</summary>
[Collection("GatewayIntegration")]
public sealed class OperationHistoryTests(GatewayFixture f) : IClassFixture<GatewayFixture>
{
    private readonly HttpClient _api = f.SignedClient();

    private async Task<string> BucketAsync()
    {
        var bucket = "hist-" + Guid.NewGuid().ToString("N")[..12];
        (await _api.PutAsync($"/v1/buckets/{bucket}", null)).EnsureSuccessStatusCode();
        return bucket;
    }

    /// <summary>History is written off the request path, so poll until the expected count lands.</summary>
    private async Task<JsonElement[]> HistoryAsync(string bucket, string key, int expected, string query = "")
    {
        JsonElement[] entries = [];
        for (var i = 0; i < 100; i++)
        {
            var body = JsonDocument.Parse(await (await _api.GetAsync($"/v1/buckets/{bucket}/objects/{key}?history{query}")).Content.ReadAsStringAsync()).RootElement;
            entries = [.. body.GetProperty("entries").EnumerateArray()];
            if (entries.Length >= expected)
            {
                return entries;
            }

            await Task.Delay(50);
        }

        return entries;
    }

    // AC1, AC2
    [Fact]
    public async Task InsertViewUpdateDelete_ThroughS3_AreAllRecordedInOrder_AndOutliveTheObject()
    {
        var bucket = await BucketAsync();
        var requestIds = new List<string>();
        var put = await f.S3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "doc.txt", ContentBody = "v1", ContentType = "text/plain" });
        requestIds.Add(put.ResponseMetadata.RequestId);
        using (var get = await f.S3.GetObjectAsync(bucket, "doc.txt"))
        {
            await new StreamReader(get.ResponseStream).ReadToEndAsync();
            requestIds.Add(get.ResponseMetadata.RequestId);
        }

        requestIds.Add((await f.S3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "doc.txt", ContentBody = "version two" })).ResponseMetadata.RequestId);
        requestIds.Add((await f.S3.DeleteObjectAsync(bucket, "doc.txt")).ResponseMetadata.RequestId);

        var entries = await HistoryAsync(bucket, "doc.txt", 4);

        Assert.Equal(["insert", "view", "update", "delete"], entries.Select(e => e.GetProperty("operation").GetString()));
        Assert.Equal(requestIds, entries.Select(e => e.GetProperty("requestId").GetString()));
        Assert.All(entries, e =>
        {
            Assert.Equal("AKIAIOSFODNN7EXAMPLE", e.GetProperty("accessKeyId").GetString());
            Assert.Equal("s3", e.GetProperty("api").GetString());
            Assert.Equal("Success", e.GetProperty("outcome").GetString());
        });
        Assert.Equal(2, entries[0].GetProperty("size").GetInt64());
        Assert.Equal(11, entries[2].GetProperty("size").GetInt64());
    }

    // AC4
    [Fact]
    public async Task MissingKeys_RecordNotFound_AndUnsignedRequestsRecordNothing()
    {
        var bucket = await BucketAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"/v1/buckets/{bucket}/objects/ghost")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/v1/buckets/{bucket}/objects/ghost")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.SignedClient(secretKey: "wrong").GetAsync($"/v1/buckets/{bucket}/objects/ghost")).StatusCode);

        var entries = await HistoryAsync(bucket, "ghost", 2);
        await Task.Delay(200); // give a wrongly recorded third entry time to land

        entries = await HistoryAsync(bucket, "ghost", 3);
        Assert.Equal(["view", "delete"], entries.Select(e => e.GetProperty("operation").GetString()));
        Assert.All(entries, e => Assert.Equal("NotFound", e.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task UnknownBucketsAndKeys_AreEmpty_NotErrors()
    {
        var r = await _api.GetAsync("/v1/buckets/no-such-bucket-here/objects/x?history");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Empty(JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Copy_RecordsAViewOfTheSourceAndAWriteOfTheDestination()
    {
        var bucket = await BucketAsync();
        await f.S3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "src", ContentBody = "copy me" });
        await f.S3.CopyObjectAsync(bucket, "src", bucket, "dst");

        var source = await HistoryAsync(bucket, "src", 2);
        var dest = await HistoryAsync(bucket, "dst", 1);

        Assert.Equal(["insert", "view"], source.Select(e => e.GetProperty("operation").GetString()));
        Assert.Equal("insert", dest.Single().GetProperty("operation").GetString());
        Assert.Equal(source[1].GetProperty("requestId").GetString(), dest[0].GetProperty("requestId").GetString());
    }

    // AC6
    [Fact]
    public async Task Paging_ReturnsEveryEntryExactlyOnce_AndFiltersByOperation()
    {
        var bucket = await BucketAsync();
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("x"));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        (await _api.PutAsync($"/v1/buckets/{bucket}/objects/k", content)).EnsureSuccessStatusCode();
        for (var i = 0; i < 249; i++)
        {
            (await _api.GetAsync($"/v1/buckets/{bucket}/objects/k")).EnsureSuccessStatusCode();
        }

        await HistoryAsync(bucket, "k", 250, "&limit=1000");
        var ids = new List<long>();
        string? cursor = null;
        do
        {
            var page = JsonDocument.Parse(await (await _api.GetAsync($"/v1/buckets/{bucket}/objects/k?history&limit=100" + (cursor is null ? "" : $"&cursor={cursor}")))
                .Content.ReadAsStringAsync()).RootElement;
            ids.AddRange(page.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(250, ids.Count);
        Assert.Equal(ids.Distinct().Order(), ids);
        Assert.Single(await HistoryAsync(bucket, "k", 1, "&operation=insert,delete"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.GetAsync($"/v1/buckets/{bucket}/objects/k?history&operation=rename")).StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_IsIgnoredFromUntrustedPeers()
    {
        var bucket = await BucketAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/buckets/{bucket}/objects/spoof");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");
        await _api.SendAsync(request);

        var entry = (await HistoryAsync(bucket, "spoof", 1)).Single();
        Assert.NotEqual("203.0.113.9", entry.GetProperty("clientIp").ToString());
    }

    // AC7 (the resolution rule the capture uses)
    [Theory]
    [InlineData("10.0.0.5", "198.51.100.7", "198.51.100.7")] // trusted proxy: the forwarded client
    [InlineData("10.0.0.5", "198.51.100.7, 10.0.0.9", "198.51.100.7")] // through two trusted hops
    [InlineData("192.0.2.1", "198.51.100.7", "192.0.2.1")] // untrusted peer: its own address
    public void ClientIp_HonorsOnlyTrustedProxies(string peer, string forwardedFor, string expected)
    {
        Assert.True(ClientAddress.TryParseNetwork("10.0.0.0/8", out var proxies));
        Assert.Equal(expected, ClientAddress.Resolve(IPAddress.Parse(peer), forwardedFor, [proxies])!.ToString());
    }

    // AC3
    [Theory]
    [InlineData(31337UL, null, 1)]
    [InlineData(84532UL, null, 720)]
    [InlineData(0UL, null, 720)] // a standalone gateway with no chain: the safe, long default
    [InlineData(31337UL, 2000, 720)] // overrides are capped at 30 days
    public void Retention_DependsOnTheNetwork(ulong chainId, int? overrideHours, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), HistoryRecorder.RetentionFor(new GatewayOptions { ChainId = chainId, History = { RetentionHours = overrideHours } }));
    }

    [Fact]
    public void Sweep_RemovesOnlyEntriesPastRetention()
    {
        using var dir = new TempHistory();
        var time = new FixedTime(DateTimeOffset.UtcNow);
        var recorder = new HistoryRecorder(dir.Store, Options.Create(new GatewayOptions { ChainId = 31337 }), NullLogger<HistoryRecorder>.Instance, time: time);
        dir.Store.Append([Entry(time.GetUtcNow() - TimeSpan.FromMinutes(90), "old"), Entry(time.GetUtcNow() - TimeSpan.FromMinutes(30), "new")]);

        Assert.Equal(1, recorder.Sweep());
        Assert.Equal(["new"], dir.Store.Query("b", "k", null, null, null, 0, 10).Select(e => e.RequestId));
    }

    // AC5
    [Fact]
    public void AFullQueue_DropsAndCounts_WithoutThrowing()
    {
        using var dir = new TempHistory();
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == HistoryRecorder.MeterName && i.Name == "blob_history.dropped") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref dropped, v));
        listener.Start();
        var recorder = new HistoryRecorder(dir.Store, Options.Create(new GatewayOptions { History = { QueueCapacity = 2 } }), NullLogger<HistoryRecorder>.Instance);

        for (var i = 0; i < 5; i++)
        {
            recorder.Record(Entry(DateTimeOffset.UtcNow, $"r{i}"));
        }

        Assert.Equal(3, Interlocked.Read(ref dropped));
        recorder.FlushNow();
        Assert.Equal(2, dir.Store.Query("b", "k", null, null, null, 0, 10).Count);
    }

    private static HistoryEntry Entry(DateTimeOffset at, string requestId) =>
        new(0, at, HistoryOperation.View, "b", "k", "s3", 1, null, null, "127.0.0.1", "AK", "t", null, requestId, "Success", 200);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempHistory : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym204-" + Guid.NewGuid().ToString("N"));

        public TempHistory() => Store = new HistoryStore(Path.Combine(_dir, "gateway-history.db"));

        public HistoryStore Store { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_dir, recursive: true);
        }
    }
}
