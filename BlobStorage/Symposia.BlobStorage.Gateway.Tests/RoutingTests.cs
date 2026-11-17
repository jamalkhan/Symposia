extern alias NodeAlias;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Amazon.S3.Model;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Routing;
using Symposia.BlobStorage.Protocol;
using Symposia.Network.Node;
using NodeManifest = NodeAlias::Symposia.BlobStorage.StorageNode.Storage.ManifestStore;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// #12 Layers 2 and 3 at the gateway: the gossip-fed routing cache, reads served by holders only the cache knows,
/// the peer-gateway and on-chain fallbacks, epoch-root disputes, and no central database on the metadata path
/// (QA TC2.1–TC2.8, TC3.3, TC3.4, TC3.7, TC4.1).
/// </summary>
public sealed class RoutingTests
{
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), $"timed out waiting for {what}");
    }

    // ── RoutingCache ──────────────────────────────────────────────────────────

    [Fact]
    public void Cache_TracksEachNodesOwnHoldings_AndAResetDropsOnlyThatNode()
    {
        var cache = new RoutingCache();
        cache.Watching("n1");
        cache.Watching("n2");
        cache.Apply("n1", "c1", held: true);
        cache.Apply("n2", "c1", held: true);
        cache.Apply("n2", "c2", held: true);
        cache.Apply("n1", "c3", held: false); // PendingGc/Orphaned or removed: not a holder

        Assert.Equal(["n1", "n2"], cache.Holders("c1").Order());
        Assert.Empty(cache.Holders("c3"));
        Assert.False(cache.IsWarm);

        cache.CaughtUp("n1");
        cache.CaughtUp("n2");
        Assert.True(cache.IsWarm);
        Assert.NotNull(cache.WarmSince);

        cache.Reset("n2");
        Assert.Equal(["n1"], cache.Holders("c1"));
        Assert.Empty(cache.Holders("c2"));

        cache.Evict("n1", "c1");
        Assert.Empty(cache.Holders("c1"));
        Assert.Equal(1, cache.CountFor("n1")); // the feed-derived count is left for the digest compare

        cache.Learn("n2", "c9"); // out of band: routable, but never counted against n2's digest
        Assert.Equal(["n2"], cache.Holders("c9"));
        Assert.Equal(0, cache.CountFor("n2"));

        cache.Forget("n1");
        Assert.DoesNotContain("n1", cache.Nodes);
    }

    // ── Feeds → cache ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Cache_WarmsFromNodeFeeds_AndFollowsWritesAndRemovals()
    {
        using var cluster = await TestCluster.StartAsync(2);
        var cache = cluster.Services.GetRequiredService<RoutingCache>();
        await WaitForAsync(() => cache.IsWarm, "the routing cache to warm");
        Assert.True(cache.WarmSince - cache.StartedAt < TimeSpan.FromSeconds(60)); // AC3's warm-up window

        var cid = await cluster.PutAsync("routed.txt", "gossiped to the gateway"u8.ToArray());
        await WaitForAsync(() => cache.Holders(cid).Count == 2, "both holders to be gossiped");

        await cluster.Connections[1].Client.DeleteBlobAsync(new DeleteBlobRequest { Cid = cid });
        await WaitForAsync(() => cache.Holders(cid).SequenceEqual([cluster.Connections[0].Id]), "the removal to be gossiped");
    }

    [Fact]
    public async Task Read_UsesAHolderOnlyTheCacheKnows_WhenTheListedReplicaIsDown()
    {
        var down = new BlackHoleHandler { Enabled = false, Matches = r => r.RequestUri!.AbsolutePath.EndsWith("/ReadBlob", StringComparison.Ordinal) };
        using var cluster = await TestCluster.StartAsync(2, handler: i => i == 0 ? down : null);
        var cache = cluster.Services.GetRequiredService<RoutingCache>();
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var body = "only the cache knows the second copy"u8.ToArray();
        var cid = await cluster.PutAsync("cache-only.txt", body);
        await WaitForAsync(() => cache.Holders(cid).Count == 2, "both holders to be gossiped");

        store.RemoveReplica(TestCluster.Bucket, "cache-only.txt", cid, cluster.Connections[1].Id); // the index lags
        down.Enabled = true;

        Assert.Equal(body, await cluster.GetAsync("cache-only.txt"));
    }

    // ── Peer gateways ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PeerRouting_RequiresTheToken_AndAnswersWithHolders()
    {
        using var cluster = await TestCluster.StartAsync(1, new Dictionary<string, string?> { ["Gateway:Routing:PeerToken"] = "peer-secret" });
        var cid = await cluster.PutAsync("peer.txt", "resolvable by peers"u8.ToArray());

        async Task<HttpResponseMessage> AskAsync(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{PeerRoutingEndpoints.Path}/{cid}");
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await cluster.Http.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync("wrong")).StatusCode);
        var answer = await (await AskAsync("peer-secret")).Content.ReadFromJsonAsync<PeerRoutingEndpoints.Answer>();
        Assert.Equal([cluster.Connections[0].Id], answer!.Nodes);
    }

    [Fact]
    public async Task Read_FallsBackToAPeerGateway_WhenThisGatewayKnowsNoReachableHolder()
    {
        GatewayFactory? peer = null;
        var down = new BlackHoleHandler { Enabled = false, Matches = r => r.RequestUri!.AbsolutePath.EndsWith("/ReadBlob", StringComparison.Ordinal) };
        using var cluster = await TestCluster.StartAsync(2,
            new Dictionary<string, string?>
            {
                ["Gateway:Routing:WatchManifests"] = "false", // a cold cache on this gateway
                ["Gateway:Routing:PeerGateways:0"] = "http://peer-gateway",
                ["Gateway:Routing:PeerToken"] = "peer-secret",
            },
            handler: i => i == 0 ? down : null,
            services: s => s.AddHttpClient(ReplicaLocator.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => peer!.Server.CreateHandler()));
        using (peer = new GatewayFactory(_ => cluster.Registry, new Dictionary<string, string?> { ["Gateway:Routing:PeerToken"] = "peer-secret" }))
        {
            var peerCache = peer.Services.GetRequiredService<RoutingCache>();
            var body = "the peer gateway has seen the gossip"u8.ToArray();
            var cid = await cluster.PutAsync("via-peer.txt", body);
            await WaitForAsync(() => peerCache.Holders(cid).Count == 2, "the peer's cache to learn both holders");

            cluster.Services.GetRequiredService<GatewayMetadataStore>().RemoveReplica(TestCluster.Bucket, "via-peer.txt", cid, cluster.Connections[1].Id);
            down.Enabled = true;

            Assert.Equal(body, await cluster.GetAsync("via-peer.txt"));
            var metrics = cluster.Services.GetRequiredService<Health.GatewayMetrics>();
            Assert.Equal(1, metrics.RoutingLookups.WithLabels("peer").Value);
            Assert.Contains(cluster.Connections[1].Id, cluster.Services.GetRequiredService<RoutingCache>().Holders(cid)); // learned
        }
    }

    // ── Layer 3: disputes and the on-chain fallback ───────────────────────────

    private sealed class FakeChain : IEpochRootClient
    {
        public Dictionary<ulong, byte[]> Roots { get; } = [];

        public Task<EpochRootSubmission> SubmitAsync(string node, ulong epoch, byte[] root, byte[] signature, CancellationToken ct = default) =>
            Task.FromResult(EpochRootSubmission.Submitted);

        public Task<byte[]?> GetRootAsync(string node, ulong epoch, CancellationToken ct = default) =>
            Task.FromResult(Roots.TryGetValue(epoch, out var root) ? root : null);

        public Task<(ulong Epoch, byte[] Root)?> GetLatestAsync(string node, CancellationToken ct = default) =>
            Task.FromResult(Roots.Count == 0 ? null : ((ulong, byte[])?)(Roots.Keys.Max(), Roots[Roots.Keys.Max()]));
    }

    private sealed class RecordingSink : Penalties.IIntegrityFailureSink
    {
        public List<(string Node, string Cid, string Detail)> Reports { get; } = [];

        public void Report(string nodeId, string cid, string detail) => Reports.Add((nodeId, cid, detail));
    }

    private static async Task<string> WriteToNodeAsync(NodeConnection node, string content)
    {
        using var call = node.Client.WriteBlob();
        await call.RequestStream.WriteAsync(new WriteBlobChunk { Header = new WriteBlobHeader { TenantId = "test-tenant", Bucket = "b", Key = "k" } });
        await call.RequestStream.WriteAsync(new WriteBlobChunk { Data = ByteString.CopyFromUtf8(content) });
        await call.RequestStream.CompleteAsync();
        return (await call.ResponseAsync).Cid;
    }

    [Fact]
    public async Task Disputes_AreDecidedByTheOnChainRoot_AndTheLocatorFindsAttestedHolders()
    {
        using var nodeFactory = new NodeFactory();
        var node = new NodeConnection("http://node-a", nodeFactory.CreateClient(), expectedNodeId: "0x00000000000000000000000000000000000000a1")
        {
            LastProbe = new ProbeResponse { Healthy = true },
            LastProbeTime = DateTimeOffset.UtcNow,
        };
        var manifest = nodeFactory.Services.GetRequiredService<NodeManifest>();
        var attested = await WriteToNodeAsync(node, "in the epoch 7 root");
        var frozen = manifest.FreezeEpoch(7);
        manifest.MarkEpoch(7, "submitted", onChain: true);
        var newer = await WriteToNodeAsync(node, "written after the epoch 7 root");

        var chain = new FakeChain();
        var cache = new RoutingCache();
        var sink = new RecordingSink();
        var metrics = new Health.GatewayMetrics();
        var disputes = new EpochRootDisputes(cache, sink, metrics, NullLogger<EpochRootDisputes>.Instance, chain);

        Assert.Equal(Attestation.Unverifiable, (await disputes.CheckAsync(node, attested, CancellationToken.None)).Outcome); // the chain has nothing yet

        chain.Roots[7] = frozen.Root;
        cache.Apply(node.Id, newer, held: true); // gossip says the node holds it
        Assert.Equal(Attestation.Attested, await disputes.ResolveAsync(node, attested, 7, CancellationToken.None));
        Assert.Equal(Attestation.NotAttested, await disputes.ResolveAsync(node, newer, 7, CancellationToken.None));
        Assert.Empty(cache.Holders(newer)); // the chain wins: evicted
        Assert.Equal((node.Id, newer), (sink.Reports.Single().Node, sink.Reports.Single().Cid)); // for the epoch's penalty/reward calculation

        chain.Roots[7] = new byte[32]; // a root the node's proof doesn't match (a lie, or a different submission)
        Assert.Equal(Attestation.NotAttested, (await disputes.CheckAsync(node, attested, CancellationToken.None)).Outcome);

        chain.Roots[7] = frozen.Root;
        var locator = new ReplicaLocator(cache, new TestCluster.ClusterRegistry([node]), disputes, new NoHttp(), metrics,
            Options.Create(new GatewayOptions()), NullLogger<ReplicaLocator>.Instance);
        Assert.Equal([node], await locator.LocateAsync(attested, new HashSet<string>(), CancellationToken.None));
        Assert.Equal(1, metrics.RoutingLookups.WithLabels("chain").Value);
        Assert.Empty(await locator.LocateAsync(newer, new HashSet<string>(), CancellationToken.None)); // too new for any root: not found, not an error
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no peer gateways are configured");
    }

    // ── No central database (AC8, TC4.1) ──────────────────────────────────────

    [Fact]
    public void MetadataPath_HasNoRelationalDatabaseDependency()
    {
        var roots = new[] { typeof(BlobGatewayApp).Assembly, typeof(NodeManifest).Assembly, typeof(Domain.ManifestMerkle).Assembly };
        var seen = new HashSet<string>();
        var pending = new Queue<AssemblyName>(roots.SelectMany(a => a.GetReferencedAssemblies()));
        while (pending.TryDequeue(out var name))
        {
            if (!seen.Add(name.Name!) || name.Name!.StartsWith("System", StringComparison.Ordinal) || name.Name.StartsWith("Microsoft", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain("Npgsql", name.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MySql", name.Name, StringComparison.OrdinalIgnoreCase);
            try
            {
                foreach (var reference in Assembly.Load(name).GetReferencedAssemblies()) pending.Enqueue(reference);
            }
            catch (FileNotFoundException)
            {
            }
        }

        Assert.Contains("Microsoft.Data.Sqlite", roots.SelectMany(a => a.GetReferencedAssemblies()).Select(a => a.Name)); // the local index is embedded SQLite
    }
}
