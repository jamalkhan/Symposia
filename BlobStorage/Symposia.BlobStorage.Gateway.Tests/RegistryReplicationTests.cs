using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// Issue #135: storage nodes from the on-chain registry, and the replica-count index that lets
/// under-replicated objects be found (and repaired) without a full scan.
/// </summary>
public sealed class RegistryReplicationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym135-" + Guid.NewGuid().ToString("N"));

    private IOptions<GatewayOptions> Options(int minCopies = 2) => Microsoft.Extensions.Options.Options.Create(new GatewayOptions
    {
        MetadataDbPath = Path.Combine(_dir, "meta.db"),
        MinCopiesPerObject = minCopies,
        ProbeIntervalSeconds = 3600,
    });

    private static ObjectRecord Record(string bucket, string key, params string[] nodes) =>
        new(bucket, key, "cid-" + bucket + key, 1, "application/octet-stream", DateTimeOffset.UtcNow, nodes);

    [Fact]
    public void ListUnderReplicated_ReturnsOnlyObjectsBelowTarget_AndTracksRepairs()
    {
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        store.PutObject(Record("b", "one", "http://a"));
        store.PutObject(Record("b", "two", "http://a", "http://b"));

        Assert.Equal(["one"], store.ListUnderReplicated(2, 100).Select(r => r.Key));
        Assert.Equal(1, store.CountUnderReplicated(2));

        store.AddReplicaForCid("cid-bone", "http://b");
        Assert.Empty(store.ListUnderReplicated(2, 100));
    }

    [Fact]
    public void Initialize_MigratesExistingDatabase_BackfillingReplicaCount()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "meta.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE objects (bucket TEXT NOT NULL, key TEXT NOT NULL, cid TEXT NOT NULL, size_bytes INTEGER NOT NULL,
                    content_type TEXT NOT NULL, last_modified TEXT NOT NULL, node_ids TEXT NOT NULL, PRIMARY KEY (bucket, key));
                INSERT INTO objects VALUES ('b', 'one', 'c1', 1, 't', '2026-01-01T00:00:00Z', 'http://a');
                INSERT INTO objects VALUES ('b', 'two', 'c2', 1, 't', '2026-01-01T00:00:00Z', 'http://a,http://b');
                INSERT INTO objects VALUES ('b', 'none', 'c3', 1, 't', '2026-01-01T00:00:00Z', '');
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        store.Initialize(); // idempotent

        // "none" has no copies left: not repairable, so not listed for repair, but still counted.
        Assert.Equal(["one"], store.ListUnderReplicated(2, 100).Select(r => r.Key));
        Assert.Equal(2, store.CountUnderReplicated(2));
    }

    [Fact]
    public void ListAllObjectsPaged_WalksEveryObjectOnceAcrossBuckets()
    {
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        foreach (var (bucket, key) in new[] { ("a", "x"), ("a", "y/z"), ("ab", "a"), ("b", "a"), ("b", "b") })
        {
            store.PutObject(Record(bucket, key, "http://a"));
        }

        var seen = new List<string>();
        (string Bucket, string Key)? cursor = null;
        while (true)
        {
            var page = store.ListAllObjectsPaged(cursor, 2);
            seen.AddRange(page.Select(r => $"{r.Bucket}/{r.Key}"));
            if (page.Count < 2)
            {
                break;
            }

            cursor = (page[^1].Bucket, page[^1].Key);
        }

        Assert.Equal(["a/x", "a/y/z", "ab/a", "b/a", "b/b"], seen);
    }

    // QA query-plan scenario: the replication paths must seek an index, not scan the table.
    [Theory]
    [InlineData("SELECT bucket FROM objects WHERE replica_count BETWEEN 1 AND 1 AND (replica_count, bucket, key) > (1, 'a', 'b') ORDER BY replica_count, bucket, key LIMIT 500", "idx_objects_replicas")]
    [InlineData("SELECT bucket FROM objects WHERE (bucket, key) > ('a', 'b') ORDER BY bucket, key LIMIT 200", "INDEX")]
    public void ReplicationQueries_UseAnIndex(string sql, string expectedIndex)
    {
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "meta.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var r = cmd.ExecuteReader();
        var plan = new List<string>();
        while (r.Read())
        {
            plan.Add(r.GetString(3));
        }

        Assert.Contains(plan, line => line.Contains(expectedIndex, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN objects", StringComparison.Ordinal) && !line.Contains("INDEX"));
    }

    [Fact]
    public async Task RegistrySync_AddsOnlyStorageNodesWhoseServicesMatchTheirAddress()
    {
        var registry = new FakeRegistry(
            new RegisteredNode("0xAAA", "http://10.0.0.1:5180"),   // storage node
            new RegisteredNode("0xBBB", "http://10.0.0.2:5190"),   // compute-only: no services document
            new RegisteredNode("0xCCC", "http://10.0.0.3:5180"),   // answers as a different node
            new RegisteredNode("0xDDD", "not a url"));
        var http = new FakeServices(new Dictionary<string, NodeServices>
        {
            ["http://10.0.0.1:5180"] = new("0xaaa", new NodeServices.StorageService("http://10.0.0.1:6180")),
            ["http://10.0.0.3:5180"] = new("0xEEE", new NodeServices.StorageService("http://10.0.0.3:6180")),
        });
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        using var nodes = new NodeRegistry(Options(), store, NullLogger<NodeRegistry>.Instance);
        var sync = new RegistryNodeSync(nodes, registry, http, Options(), NullLogger<RegistryNodeSync>.Instance);

        await sync.SyncOnceAsync(CancellationToken.None);

        var node = Assert.Single(nodes.All);
        Assert.Equal("http://10.0.0.1:6180", node.Url);
        Assert.Equal("0xAAA", node.ExpectedNodeId);
    }

    [Fact]
    public void NodeServices_GrpcUrl_UsesAdvertisedHostWithGrpcPort()
    {
        Assert.Equal("http://10.0.0.213:6180", NodeServices.GrpcUrl("http://10.0.0.213:5180", 5180, 6180));
        Assert.Equal("http://127.0.0.1:6181", NodeServices.GrpcUrl("", 5181, 6181));
        Assert.Equal("http://10.0.0.213:5180", NodeServices.GrpcUrl("http://10.0.0.213:5180", 5180, 0));
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

    private sealed class FakeRegistry(params RegisteredNode[] nodes) : INodeRegistryClient
    {
        public Task<IReadOnlyList<RegisteredNode>> ListNodesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RegisteredNode>>(nodes);

        public Task<NodeRegistryStatus> GetStatusAsync(string address, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RegisterAsync(string address, string endpoint, ulong nonce, byte[] signature, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeServices(Dictionary<string, NodeServices> byEndpoint) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(byEndpoint));

        private sealed class Handler(Dictionary<string, NodeServices> byEndpoint) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var endpoint = request.RequestUri!.GetLeftPart(UriPartial.Authority);
                return Task.FromResult(byEndpoint.TryGetValue(endpoint, out var services) && request.RequestUri.AbsolutePath == NodeServices.Path
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(services, JsonSerializerOptions.Web)) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
