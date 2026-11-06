using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// Issue #135 code-review findings: replica lists must never be lost, written onto a newer
/// version of a key, or stranded on a node's old address.
/// </summary>
public sealed class ReplicaConsistencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym135c-" + Guid.NewGuid().ToString("N"));

    private IOptions<GatewayOptions> Options() => Microsoft.Extensions.Options.Options.Create(new GatewayOptions
    {
        MetadataDbPath = Path.Combine(_dir, "meta.db"),
        MinCopiesPerObject = 2,
        ProbeIntervalSeconds = 3600,
    });

    private GatewayMetadataStore Store()
    {
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        return store;
    }

    private static ObjectRecord Record(string bucket, string key, params string[] nodes) =>
        new(bucket, key, "cid-" + bucket + key, 1, "application/octet-stream", DateTimeOffset.UtcNow, nodes);

    [Fact]
    public void ReplicaUpdates_FromAStaleRecord_DoNotTouchANewerVersionOfTheKey()
    {
        var store = Store();
        var v1 = Record("b", "k", "http://a", "http://b");
        store.PutObject(v1);
        store.PutObject(v1 with { Cid = "cid-v2", NodeIds = ["http://c"] }); // overwritten meanwhile

        Assert.False(store.RemoveReplica("b", "k", v1.Cid, "http://a"));
        Assert.False(store.UpdateNodeIds("b", "k", v1.Cid, ["http://a"]));
        Assert.Equal(["http://c"], store.GetObject("b", "k")!.NodeIds);
    }

    [Fact]
    public void RemoveReplica_ReReadsTheCurrentList_SoTwoRemovalsFromOneSnapshotBothStick()
    {
        var store = Store();
        var record = Record("b", "k", "http://a", "http://b", "http://c");
        store.PutObject(record);

        // GET failover over two NotFound replicas: both calls hold the same stale snapshot.
        Assert.True(store.RemoveReplica("b", "k", record.Cid, "http://a"));
        Assert.True(store.RemoveReplica("b", "k", record.Cid, "http://b"));

        Assert.Equal(["http://c"], store.GetObject("b", "k")!.NodeIds);
    }

    [Fact]
    public void ListUnderReplicated_PagesPastObjectsThatCantBeRepaired()
    {
        var store = Store();
        for (var i = 0; i < 5; i++)
        {
            store.PutObject(Record("b", $"lost{i}"));            // no copies left: not repairable
            store.PutObject(Record("b", $"short{i}", "http://a"));
        }

        var seen = new List<string>();
        (int, string, string)? cursor = null;
        while (true)
        {
            var page = store.ListUnderReplicated(2, 2, cursor);
            seen.AddRange(page.Select(r => r.Key));
            if (page.Count < 2)
            {
                break;
            }

            cursor = (page[^1].NodeIds.Count, page[^1].Bucket, page[^1].Key);
        }

        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"short{i}"), seen);
        Assert.Equal(10, store.CountUnderReplicated(2));
    }

    [Fact]
    public void UrlKeyedMetadata_IsMigratedToNodeIds_AndAddressChangesTouchNoMetadata()
    {
        var store = Store();
        store.PutObject(Record("b", "k", "http://10.0.0.5:6180", "http://10.0.0.6:6180"));
        store.EnqueueNodeDeletion("cid-gone", "http://10.0.0.6:6180");
        using var nodes = new NodeRegistry(Options(), store, NullLogger<NodeRegistry>.Instance, new NetworkPolicy(Options(), NullLogger<NetworkPolicy>.Instance));

        // First sight of each registry node migrates #135-era URL references to its address.
        nodes.Sync([new("0xA", "http://10.0.0.5:6180"), new("0xB", "http://10.0.0.6:6180")]);
        Assert.Equal(["0xA", "0xB"], store.GetObject("b", "k")!.NodeIds);
        Assert.Equal("0xB", Assert.Single(store.GetPendingDeletions(10)).NodeUrl);

        // B moves: only the connection changes.
        nodes.Sync([new("0xA", "http://10.0.0.5:6180"), new("0xB", "http://10.0.0.7:6180")]);
        Assert.Equal(["http://10.0.0.5:6180", "http://10.0.0.7:6180"], nodes.All.Select(n => n.Url).Order());
        Assert.Equal("0xB", nodes.All.Single(n => n.Url.EndsWith(".7:6180")).Id);
        Assert.Equal(["0xA", "0xB"], store.GetObject("b", "k")!.NodeIds);
        Assert.Equal(0, store.CountUnderReplicated(2));
    }

    [Fact]
    public void UrlReusedByAnotherNode_IsNotCreditedWithThePreviousNodesCopies()
    {
        var store = Store();
        using var nodes = new NodeRegistry(Options(), store, NullLogger<NodeRegistry>.Instance, new NetworkPolicy(Options(), NullLogger<NetworkPolicy>.Instance));
        nodes.Sync([new("0xA", "http://10.0.0.5:6180")]);
        store.PutObject(Record("b", "k", "0xA"));

        nodes.Sync([new("0xC", "http://10.0.0.5:6180")]);

        var node = Assert.Single(nodes.All);
        Assert.Equal("0xC", node.Id);
        Assert.Equal(["0xA"], store.GetObject("b", "k")!.NodeIds);
    }

    [Fact]
    public async Task NodeMissingFromItsSourcePastTheGracePeriod_IsDropped()
    {
        var store = Store();
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            MetadataDbPath = Path.Combine(_dir, "meta.db"),
            ProbeIntervalSeconds = 3600,
            NodeGracePeriodSeconds = 1,
        });
        using var nodes = new NodeRegistry(options, store, NullLogger<NodeRegistry>.Instance, new NetworkPolicy(options, NullLogger<NetworkPolicy>.Instance));
        nodes.Sync([new("0xA", "http://10.0.0.5:6180"), new("0xB", "http://10.0.0.6:6180")]);

        await Task.Delay(1200);
        nodes.Sync([new("0xA", "http://10.0.0.5:6180")]);

        Assert.Equal(["0xA"], nodes.All.Select(n => n.Id));
    }

    [Fact]
    public async Task Migration_IsSafeWhenSeveralProcessesStartTogether()
    {
        Directory.CreateDirectory(_dir);
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "meta.db")}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE objects (bucket TEXT NOT NULL, key TEXT NOT NULL, cid TEXT NOT NULL, size_bytes INTEGER NOT NULL, " +
                "content_type TEXT NOT NULL, last_modified TEXT NOT NULL, node_ids TEXT NOT NULL, PRIMARY KEY (bucket, key)); " +
                "INSERT INTO objects VALUES ('b', 'one', 'c1', 1, 't', '2026-01-01T00:00:00Z', 'http://a');";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => new GatewayMetadataStore(Options()).Initialize())));

        Assert.Equal(["one"], new GatewayMetadataStore(Options()).ListUnderReplicated(2, 10).Select(r => r.Key));
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
