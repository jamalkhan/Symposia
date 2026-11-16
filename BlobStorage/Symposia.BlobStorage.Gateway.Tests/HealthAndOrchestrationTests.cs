using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Symposia.BlobStorage.Gateway.Health;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Gateway orchestration, health, readiness, metrics, drain and request logging (issue #1).</summary>
[Collection("GatewayIntegration")]
public sealed class HealthAndOrchestrationTests
{
    private const string MetricsToken = "operator-scrape-token";

    private static readonly Dictionary<string, string?> Config = new()
    {
        ["Gateway:MetricsBearerToken"] = MetricsToken,
        ["Gateway:TenantQuotaBytes:quota-tenant"] = "100",
        ["Gateway:Credentials:1:AccessKeyId"] = "AKIAQUOTA",
        ["Gateway:Credentials:1:SecretAccessKey"] = "quota-secret",
        ["Gateway:Credentials:1:TenantId"] = "quota-tenant",
    };

    // ── /health ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Health_IsUnauthenticated_AndReportsVersionAndUptime()
    {
        using var cluster = await TestCluster.StartAsync(1, Config);

        var started = DateTime.UtcNow;
        var response = await cluster.Http.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(100) * 10, "well inside a load balancer's budget");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.True(body.RootElement.TryGetProperty("version", out _));
        Assert.True(body.RootElement.GetProperty("uptime_seconds").GetInt64() >= 0);
    }

    // ── /ready ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ready_WhenReady_ReturnsDocumentedShape()
    {
        using var cluster = await TestCluster.StartAsync(2, Config);

        var response = await cluster.Http.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("node_cache_size").GetInt32());
        Assert.True(body.RootElement.GetProperty("metadata_index_connected").GetBoolean());
        Assert.True(body.RootElement.GetProperty("min_eligible_nodes_available").GetBoolean());
    }

    [Fact]
    public async Task Ready_WhileDraining_Returns503WithReason_AndInFlightTrafficStillWorks()
    {
        using var cluster = await TestCluster.StartAsync(1, Config);
        await cluster.PutAsync("drain/key", "before"u8.ToArray());

        cluster.Services.GetRequiredService<ShutdownState>().BeginDrain();
        var response = await cluster.Http.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("draining", body.RootElement.GetProperty("reason").GetString());
        Assert.True(body.RootElement.GetProperty("retry_after_seconds").GetInt32() > 0);
        // The load balancer drains on /ready; requests that still arrive are served.
        Assert.Equal("before"u8.ToArray(), await cluster.GetAsync("drain/key"));
    }

    private sealed class Registry(bool loaded, params NodeConnection[] nodes) : INodeRegistry
    {
        public IReadOnlyList<NodeConnection> All => nodes;
        public IReadOnlyList<NodeConnection> Healthy => nodes.Where(n => n.IsHealthy).ToList();
        public bool CacheLoaded => loaded;
        public NodeConnection? SelectForRead(IEnumerable<string> nodeUrls) => null;
        public NodeConnection AddNode(string url) => throw new NotSupportedException();
        public bool RemoveNode(string url) => false;
    }

    private static NodeConnection Healthy(string url) => new(url)
    {
        LastProbe = new ProbeResponse { Healthy = true, AvailableStorageBytes = 1 << 30 },
        LastProbeTime = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task NotReadyReason_CoversEachDocumentedCondition()
    {
        using var cluster = await TestCluster.StartAsync(1, Config);
        var index = cluster.Services.GetRequiredService<MetadataIndexMonitor>();
        var running = new ShutdownState();

        Assert.Equal("node_cache_not_loaded", HealthEndpoints.NotReadyReason(new Registry(false, Healthy("http://a")), index, running, 1)?.Reason);
        Assert.Equal("insufficient_eligible_nodes", HealthEndpoints.NotReadyReason(new Registry(true, Healthy("http://a")), index, running, 2)?.Reason);
        Assert.Null(HealthEndpoints.NotReadyReason(new Registry(true, Healthy("http://a"), Healthy("http://b")), index, running, 2));

        var draining = new ShutdownState();
        draining.BeginDrain();
        Assert.Equal("draining", HealthEndpoints.NotReadyReason(new Registry(true, Healthy("http://a")), index, draining, 1)?.Reason);
    }

    [Fact]
    public async Task NotReadyReason_MetadataIndexUnreachable()
    {
        using var cluster = await TestCluster.StartAsync(1, Config);
        var monitor = new MetadataIndexMonitor(new GatewayMetadataStore(Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            // A directory, not a database file: every connection fails.
            MetadataDbPath = cluster.Gateway.DataDir,
        })), Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataIndexMonitor>.Instance);
        monitor.Check();

        Assert.False(monitor.IsConnected);
        Assert.Equal("metadata_index_not_connected",
            HealthEndpoints.NotReadyReason(new Registry(true, Healthy("http://a")), monitor, new ShutdownState(), 1)?.Reason);
    }

    [Fact]
    public async Task NodeRegistry_IsNotLoadedUntilEveryNodeIsProbed()
    {
        using var cluster = await TestCluster.StartAsync(1, Config);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());
        using var registry = new NodeRegistry(options, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<NodeRegistry>.Instance,
            new NetworkPolicy(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<NetworkPolicy>.Instance));

        Assert.False(registry.CacheLoaded); // no nodes yet
        registry.Sync([new StorageNodeDescriptor("http://127.0.0.1:1", "http://127.0.0.1:1", null)]);
        var probed = await PollAsync(() => registry.CacheLoaded);

        Assert.True(probed, "a failed probe still loads the cache: the node is known to be down");
        Assert.False(registry.All[0].IsHealthy);
    }

    // ── /metrics ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Metrics_RequiresTheOperatorToken()
    {
        using var cluster = await TestCluster.StartAsync(1, Config);

        var anonymous = await cluster.Http.GetAsync("/metrics");
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/metrics") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "nope") } };
        var wrongToken = await cluster.Http.SendAsync(wrong);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
    }

    [Fact]
    public async Task Metrics_WithToken_ExposesEveryDocumentedFamily()
    {
        using var cluster = await TestCluster.StartAsync(2, Config);
        await cluster.PutAsync("metrics/key", "payload"u8.ToArray());
        await cluster.GetAsync("metrics/key");

        var text = await ScrapeAsync(cluster);

        foreach (var family in new[]
                 {
                     "gateway_requests_total", "gateway_request_duration_seconds", "gateway_request_bytes_total",
                     "gateway_node_selection_score", "gateway_node_errors_total", "gateway_read_failovers_total",
                     "gateway_write_quorum_reached_total", "gateway_write_quorum_failed_total", "gateway_write_fanout_duration_seconds",
                     "gateway_node_cache_size", "gateway_metadata_index_lag_seconds", "gateway_eligible_nodes_count",
                 })
        {
            Assert.Contains($"# TYPE {family} ", text);
        }

        Assert.Contains("gateway_requests_total{method=\"PUT\",status_code=\"200\",operation=\"PutObject\"} 1", text);
        Assert.Contains("gateway_write_quorum_reached_total 1", text);
        Assert.Contains("gateway_node_cache_size 2", text);
    }

    private static async Task<string> ScrapeAsync(TestCluster cluster)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", MetricsToken) } };
        var response = await cluster.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // ── Read path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_NodeFailsMidStream_FailsOverTransparently()
    {
        // Whichever replica serves first dies after 100 KB; the read resumes on the other at that byte.
        var cuts = new StrongBox<int>(1);
        using var cluster = await TestCluster.StartAsync(2, Config, handler: _ => new CutReadsHandler(100 * 1024, cuts));
        var body = new byte[300 * 1024];
        Random.Shared.NextBytes(body);
        await cluster.PutAsync("failover/key", body);

        var read = await cluster.GetAsync("failover/key");

        Assert.Equal(body, read);
        Assert.Equal(-1, cuts.Value); // the cut happened
        Assert.Contains("gateway_read_failovers_total 1", await ScrapeAsync(cluster));
    }

    [Fact]
    public async Task Get_RangeRead_FailsOverAtTheRightOffset()
    {
        var cuts = new StrongBox<int>(1);
        using var cluster = await TestCluster.StartAsync(2, Config, handler: _ => new CutReadsHandler(70 * 1024, cuts));
        var body = new byte[256 * 1024];
        Random.Shared.NextBytes(body);
        await cluster.PutAsync("failover/range", body);

        using var response = await cluster.S3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = TestCluster.Bucket, Key = "failover/range", ByteRange = new ByteRange(1000, 200_999),
        });
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);

        Assert.Equal(body[1000..201_000], buffer.ToArray());
    }

    [Fact]
    public async Task Get_AllReplicasUnreachable_Returns503WithRetryAfter()
    {
        var holes = new List<BlackHoleHandler>();
        using var cluster = await TestCluster.StartAsync(2, Config, handler: _ =>
        {
            var hole = new BlackHoleHandler { Enabled = false };
            holes.Add(hole);
            return hole;
        });
        await cluster.PutAsync("down/key", "data"u8.ToArray());
        holes.ForEach(h => h.Enabled = true);

        using var http = cluster.Signed();
        var response = await http.GetAsync($"/{TestCluster.Bucket}/down/key");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Contains("temporarily unavailable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_EveryReplicaMissingTheBlob_SaysLostNotUnavailable()
    {
        using var cluster = await TestCluster.StartAsync(1, Config);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        // Metadata points at a blob no node holds.
        store.PutObject(new ObjectRecord(TestCluster.Bucket, "lost/key", new string('a', 64), 4, "text/plain", DateTimeOffset.UtcNow,
            [cluster.Connections[0].Id]));

        using var http = cluster.Signed();
        var response = await http.GetAsync($"/{TestCluster.Bucket}/lost/key");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("ObjectDataLost", await response.Content.ReadAsStringAsync());
    }

    // ── Write path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_OverQuota_Returns507BeforeAnyNodeIsContacted()
    {
        var writes = 0;
        using var cluster = await TestCluster.StartAsync(1, Config, handler: _ => new CountingHandler(() => Interlocked.Increment(ref writes)));
        using var s3 = GatewayFixture.S3ClientFor(cluster.Gateway.Server, "AKIAQUOTA", "quota-secret");
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = "quota-bucket", UseClientRegion = true });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "quota-bucket", Key = "a", InputStream = new MemoryStream(new byte[60]), UseChunkEncoding = false });
        var before = writes;

        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = "quota-bucket", Key = "b", InputStream = new MemoryStream(new byte[60]), UseChunkEncoding = false,
        }));

        Assert.Equal((HttpStatusCode)507, ex.StatusCode);
        Assert.Equal("QuotaExceeded", ex.ErrorCode);
        Assert.Equal(before, writes);
        Assert.Equal(60, cluster.Services.GetRequiredService<GatewayMetadataStore>().TenantUsageBytes("quota-tenant"));
    }

    [Fact]
    public async Task TenantUsage_FollowsOverwritesAndDeletes()
    {
        using var cluster = await TestCluster.StartAsync(1, Config);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var before = store.TenantUsageBytes("test-tenant");

        await cluster.PutAsync("usage/key", new byte[40]);
        await cluster.PutAsync("usage/key", new byte[10]);
        Assert.Equal(before + 10, store.TenantUsageBytes("test-tenant"));

        // Deleted bytes stay billed until physically deleted (#16 FR9), not at delete time.
        await cluster.S3.DeleteObjectAsync(TestCluster.Bucket, "usage/key");
        Assert.Equal(before + 10, store.TenantUsageBytes("test-tenant"));
        var version = store.Versions(TestCluster.Bucket, "usage/key").Single(v => v.Kind == "deleted");
        store.QueueVersionPurge(version);
        foreach (var entry in store.DueDeletions(100, TimeSpan.Zero, 0).Where(e => e.Cid == version.Cid))
        {
            store.RemoveFromGcQueue(entry.Id);
        }

        store.ConfirmReplicaDeleted(version.Cid);
        Assert.Equal(before, store.TenantUsageBytes("test-tenant"));
    }

    [Fact]
    public async Task Put_NodeTransientlyUnavailableAtOpen_IsRetriedOnce()
    {
        // The first WriteBlob call to the node fails to connect; the retry succeeds and the node counts.
        var failures = new StrongBox<int>(1);
        using var cluster = await TestCluster.StartAsync(1, Config, handler: _ => new BlackHoleHandler
        {
            Matches = r => r.RequestUri!.AbsolutePath.EndsWith("/WriteBlob", StringComparison.Ordinal) && Interlocked.Decrement(ref failures.Value) >= 0,
        });

        var etag = await cluster.PutAsync("retry/key", "retried"u8.ToArray());

        Assert.NotEmpty(etag);
        Assert.Equal("retried"u8.ToArray(), await cluster.GetAsync("retry/key"));
    }

    // ── Structured request log ───────────────────────────────────────────────

    [Fact]
    public async Task EveryRequest_LogsOneLineWithTheRequiredFields()
    {
        var sink = new CapturingLoggerProvider();
        using var cluster = await TestCluster.StartAsync(1, Config, services: s => s.AddSingleton<ILoggerProvider>(sink));
        sink.Clear();

        await cluster.PutAsync("log/key", "logged"u8.ToArray());
        await cluster.GetAsync("log/key");

        var lines = sink.Entries.Where(e => e.Category == "Symposia.BlobStorage.Gateway.RequestLog").ToList();
        Assert.Equal(2, lines.Count);
        var put = lines[0].State.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("test-tenant", put["TenantId"]);
        Assert.Equal("static:AKIAIOSFODNN7EXAMPLE", put["CredentialId"]); // the credential's ID (#3), not its access key
        Assert.Equal("PutObject", put["Operation"]);
        Assert.Equal(TestCluster.Bucket, put["Bucket"]);
        Assert.Equal("log/key", put["Key"]);
        Assert.Equal(200, put["StatusCode"]);
        Assert.Equal(6L, put["BytesIn"]);
        Assert.Equal(cluster.Connections[0].Id, put["SelectedNodeId"]);
        Assert.True(put.ContainsKey("LatencyMs") && put.ContainsKey("RequestId") && put.ContainsKey("Error"));
        var get = lines[1].State.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("GetObject", get["Operation"]);
        Assert.Equal(6L, get["BytesOut"]);
    }

    private static async Task<bool> PollAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        return condition();
    }
}

/// <summary>Calls <paramref name="onWrite"/> for every WriteBlob call that reaches the node.</summary>
internal sealed class CountingHandler(Action onWrite) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/WriteBlob", StringComparison.Ordinal) == true)
        {
            onWrite();
        }

        return base.SendAsync(request, ct);
    }
}

/// <summary>Keeps every log entry with its structured state, for assertions on log fields.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Entry> _entries = new();

    public sealed record Entry(string Category, LogLevel Level, IReadOnlyList<KeyValuePair<string, object?>> State, string Message);

    public IReadOnlyList<Entry> Entries => [.. _entries];

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose() { }

    private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new Entry(category, logLevel,
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? [], formatter(state, exception)));
    }
}
