extern alias NodeAlias;

using System.Text.Json;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.Domain.Erasure;
using Symposia.BlobStorage.Gateway.Erasure;
using Symposia.BlobStorage.Gateway.Integrity;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Penalties;
using Symposia.BlobStorage.Gateway.Placement;
using Symposia.BlobStorage.Gateway.Quorum;
using Symposia.BlobStorage.Gateway.Repair;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Redundancy, placement rules and auto-repair (issue #8).</summary>
[Collection("GatewayIntegration")]
public sealed class RedundancyTests
{
    // ── Reed–Solomon (FR5) ────────────────────────────────────────────────────

    [Theory]
    [InlineData(4, 2)]
    [InlineData(6, 3)]
    [InlineData(10, 4)]
    public void ReedSolomon_AnyKShardsRebuildTheRest(int k, int m)
    {
        var codec = new ReedSolomon(k, m);
        var length = 1000;
        var shards = Enumerable.Range(0, k + m).Select(_ => new byte[length]).ToArray();
        for (var d = 0; d < k; d++) Random.Shared.NextBytes(shards[d]);
        codec.Encode(shards, length);
        var original = shards.Select(s => s.ToArray()).ToArray();

        for (var trial = 0; trial < 20; trial++)
        {
            var lost = Enumerable.Range(0, k + m).OrderBy(_ => Random.Shared.Next()).Take(m).ToHashSet();
            var copy = original.Select((s, i) => lost.Contains(i) ? new byte[length] : s.ToArray()).ToArray();
            codec.Reconstruct(copy, [.. Enumerable.Range(0, k + m).Select(i => !lost.Contains(i))], length);

            for (var i = 0; i < k + m; i++) Assert.Equal(original[i], copy[i]);
        }
    }

    [Fact]
    public void ReedSolomon_FewerThanKShards_Throws()
    {
        var codec = new ReedSolomon(4, 2);
        var shards = Enumerable.Range(0, 6).Select(_ => new byte[8]).ToArray();
        Assert.Throws<InvalidOperationException>(() => codec.Reconstruct(shards, [true, true, true, false, false, false], 8));
    }

    [Fact]
    public void ErasureScheme_ParsesAndBoundsTheQuorum()
    {
        Assert.True(ErasureScheme.TryParse("rs:4+2", out var scheme));
        Assert.Equal(6, scheme.TotalShards);
        Assert.Equal(5, scheme.WriteQuorum);
        Assert.False(ErasureScheme.TryParse("rs:4", out _));
        Assert.False(ErasureScheme.TryParse("rs:30+5", out _));
    }

    // ── Merkle proofs (FR6.4) ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(5 * 65536 + 7)]
    public void Merkle_EveryLeafProves_AndATamperedLeafDoesNot(int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        var builder = new MerkleBuilder();
        builder.Append(data);
        var leaves = builder.Finish();
        var root = MerkleTree.Root(leaves);
        Assert.Equal(MerkleTree.LeafCount(size), leaves.Count);

        for (var i = 0; i < leaves.Count; i++)
        {
            var leaf = data.AsSpan(i * MerkleTree.LeafSize, Math.Min(MerkleTree.LeafSize, size - i * MerkleTree.LeafSize)).ToArray();
            var proof = MerkleTree.Proof(leaves, i);
            Assert.True(MerkleTree.Verify(leaf, i, leaves.Count, proof, root));
            if (leaf.Length > 0)
            {
                leaf[0] ^= 1;
                Assert.False(MerkleTree.Verify(leaf, i, leaves.Count, proof, root));
            }
        }
    }

    // ── Trigger classification (FR4, AC5, AC6) ────────────────────────────────

    [Theory]
    [InlineData(4, 1, 3, 0, 0, RepairPriority.P0)]   // one healthy copy left: P0 even though others are in grace
    [InlineData(4, 0, 0, 0, 0, RepairPriority.P0)]   // none left
    [InlineData(4, 3, 3, 3, 0, RepairPriority.P0)]   // below minimum, all remaining on stage 2+
    [InlineData(4, 3, 3, 0, 0, RepairPriority.P1)]   // first drop below minimum
    [InlineData(4, 4, 4, 0, 1, RepairPriority.P2)]   // at minimum, a copy on a stage-1 node
    public void Classify_MapsEachTriggerToOneTier(int target, int online, int counted, int stage2, int stage1, RepairPriority expected)
    {
        var tier = RepairClassifier.Classify(new ReplicaObservation(target, online, counted, stage2, stage1));
        Assert.Equal(expected, tier?.Priority);
    }

    [Fact]
    public void Classify_NodeOfflineWithinGrace_CreatesNothing()
    {
        // Two of four copies offline but inside the grace period: they still count (AC6).
        Assert.Null(RepairClassifier.Classify(new ReplicaObservation(4, 2, 4, 0, 0)));
    }

    [Fact]
    public void Classify_PlacementAndGrowthTiers()
    {
        Assert.Equal((RepairPriority.P2, RepairReason.PlacementImprovement), RepairClassifier.Classify(new(4, 4, 4, 0, 0, SoftViolationFixable: true)));
        Assert.Equal((RepairPriority.P3, RepairReason.PlacementImprovement), RepairClassifier.Classify(new(4, 4, 4, 0, 0, OverflowFixable: true)));
        Assert.Equal((RepairPriority.P3, RepairReason.OpportunisticRedundancy), RepairClassifier.Classify(new(4, 4, 4, 0, 0, CanGrow: true)));
    }

    // ── Fault domains (FR2, AC2, AC3) ─────────────────────────────────────────

    private sealed class Registry(params NodeConnection[] nodes) : INodeRegistry
    {
        public IReadOnlyList<NodeConnection> All => nodes;
        public IReadOnlyList<NodeConnection> Healthy => nodes.Where(n => n.IsHealthy).ToList();
        public NodeConnection? SelectForRead(IEnumerable<string> nodeUrls) => null;
        public NodeConnection AddNode(string url) => throw new NotSupportedException();
        public bool RemoveNode(string url) => false;
    }

    private static NodeConnection Node(string id, string region, string ip, long asn = 0, string facility = "") => new($"http://{id}")
    {
        LastProbe = new ProbeResponse { Healthy = true, AvailableStorageBytes = 1 << 30, Region = region, PublicIp = ip, Asn = asn, FacilityId = facility },
        LastProbeTime = DateTimeOffset.UtcNow,
    };

    private static PlacementEngine Engine(params NodeConnection[] nodes) =>
        new(new Registry(nodes), Options.Create(new GatewayOptions { Redundancy = { Mode = RedundancyMode.Network } }));

    [Fact]
    public void Placement_NeverPutsTwoCopiesBehindOnePublicIp()
    {
        // Five nodes, two pairs sharing an IP: a global blob needs 4 copies, so only 3 distinct IPs → 3 copies.
        var engine = Engine(
            Node("a", "east", "198.51.100.1"), Node("b", "west", "198.51.100.1"),
            Node("c", "central", "198.51.100.2"), Node("d", "east", "198.51.100.2"),
            Node("e", "west", "203.0.113.9"));

        var plan = engine.PlaceNew(QuorumPolicy.Resolve([]), null);

        Assert.Equal(3, plan.Targets.Count);
        Assert.Equal(3, plan.Targets.Select(t => t.PublicIp).Distinct().Count());
        Assert.Contains(plan.Gaps, g => g.Rule == "capacity");
    }

    [Fact]
    public void Placement_SoftRulesBendOnlyWhenNeeded_AndEveryBendIsRecorded()
    {
        var sameAsn = Engine(
            Node("a", "east", "198.51.100.1", asn: 64500), Node("b", "west", "198.51.100.2", asn: 64500),
            Node("c", "central", "203.0.113.1", asn: 64501), Node("d", "south", "192.0.2.1", asn: 64502));

        var plan = sameAsn.PlaceNew(QuorumPolicy.Resolve([]), null);

        Assert.Equal(4, plan.Targets.Count);
        Assert.Contains(plan.Gaps, g => g.Rule == "asn");
        Assert.Contains(plan.Gaps, g => g.Rule == "subnet"); // a and b share 198.51.100.0/24

        var roomy = Engine(
            Node("a", "east", "198.51.100.1", asn: 64500), Node("b", "west", "198.51.100.2", asn: 64500),
            Node("c", "central", "203.0.113.1", asn: 64501), Node("d", "south", "192.0.2.1", asn: 64502),
            Node("e", "north", "100.64.0.1", asn: 64503));
        Assert.Empty(roomy.PlaceNew(QuorumPolicy.Resolve([]), null).Gaps);
    }

    [Fact]
    public void Placement_SkipsStage3Nodes()
    {
        var penalized = Node("bad", "east", "198.51.100.9");
        penalized.PenaltyStage = 3;
        var engine = Engine(penalized, Node("a", "east", "198.51.100.1"), Node("b", "west", "203.0.113.1"),
            Node("c", "central", "192.0.2.1"), Node("d", "south", "100.64.0.1"));

        Assert.DoesNotContain(penalized, engine.PlaceNew(QuorumPolicy.Resolve([]), null).Targets);
    }

    [Fact]
    public void ReadRouting_ExcludesStage3_AndDeprioritizesStage2()
    {
        var healthy = Node("ok", "east", "");
        var degraded = Node("deg", "east", "");
        degraded.PenaltyStage = 2;
        degraded.LastProbeTime = DateTimeOffset.UtcNow.AddSeconds(5); // probed more recently, still second
        var slashed = Node("cut", "east", "");
        slashed.PenaltyStage = 3;

        var order = S3.ReplicaReader.Order(new Registry(degraded, slashed, healthy), [degraded.Id, slashed.Id, healthy.Id]);

        Assert.Equal([healthy, degraded], order);
    }

    // ── Region assignment precedence (FR1.1) ──────────────────────────────────

    [Fact]
    public void RegionResolver_ObjectThenLongestFolderThenBucket()
    {
        var store = Store();
        store.CreateBucket("b", "t");
        store.SetBucketRegions("b", ["east"]);
        store.SetPrefixRegions("b", "eu/", ["eu-west"]);
        store.SetPrefixRegions("b", "eu/de/", ["eu-central"]);
        var resolver = new RegionAssignmentResolver(store);

        Assert.Equal(["east"], resolver.Resolve("b", "plain.txt"));
        Assert.Equal(["eu-west"], resolver.Resolve("b", "eu/fr/x"));
        Assert.Equal(["eu-central"], resolver.Resolve("b", "eu/de/x"));
        Assert.Equal(["ap-south"], resolver.Resolve("b", "eu/de/x", ["ap-south"]));
    }

    // ── Priority queue and SLA audit (FR4.1, FR4.5) ───────────────────────────

    [Fact]
    public async Task Queue_DrainsP0First_EscalatesUpward_NeverDowngrades_AndAuditsEveryTask()
    {
        var store = Store();
        var queue = new RepairQueue(store);

        Assert.True(queue.Enqueue(new RepairTask("c3", null, RepairReason.OpportunisticRedundancy)));
        Assert.True(queue.Enqueue(new RepairTask("c1", null, RepairReason.UnderReplicated)));
        Assert.False(queue.Enqueue(new RepairTask("c1", null, RepairReason.PlacementImprovement))); // P2 never downgrades P1
        Assert.True(queue.Enqueue(new RepairTask("c3", null, RepairReason.UnderReplicated) { Priority = RepairPriority.P0 })); // escalation

        var first = await queue.ReadAsync(CancellationToken.None);
        var second = await queue.ReadAsync(CancellationToken.None);
        Assert.Equal(("c3", RepairPriority.P0), (first.Cid, first.Priority));
        Assert.Equal(("c1", RepairPriority.P1), (second.Cid, second.Priority));
        queue.Done(first);
        queue.Done(second);

        var report = store.RepairSlaReport(DateTimeOffset.UtcNow.AddHours(-1), RepairQueue.Slas);
        Assert.Equal(1, report[0].Completed);
        Assert.Equal(1, report[0].WithinSla);
        Assert.Equal(1, report[1].Completed);
        Assert.Empty(store.OpenRepairTasks(10)); // the superseded P3 row closed too
    }

    [Fact]
    public void Queue_Escalation_ClosesTheSupersededAuditRow() // review: Lenny #8
    {
        var store = Store();
        var queue = new RepairQueue(store);
        queue.Enqueue(new RepairTask("esc", null, RepairReason.PlacementImprovement));
        queue.Enqueue(new RepairTask("esc", null, RepairReason.UnderReplicated) { Priority = RepairPriority.P0 });

        Assert.Single(store.OpenRepairTasks(10));
    }

    [Fact]
    public async Task Challenge_ProofCapableNodeAnsweringWithoutAProof_Fails() // review: Carl #1
    {
        using var cluster = await TestCluster.StartAsync(1);
        await cluster.PutAsync("challenge/dodge", new byte[200_000]);
        var record = cluster.Services.GetRequiredService<GatewayMetadataStore>().GetObject(TestCluster.Bucket, "challenge/dodge")!;
        var node = cluster.Connections[0];

        Assert.Equal(ChallengeResult.Passed, await ChallengeScheduler.ChallengeAsync(node, record, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.True(node.ProofCapable);
        // The same node later claiming not to support proofs would only be echoing the CID it was sent.
        var unprovable = record with { MerkleRoot = new string('0', 64) };
        Assert.Equal(ChallengeResult.Failed, await ChallengeScheduler.ChallengeAsync(node, unprovable, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public void Queue_RehydratesOpenTasksAfterARestart()
    {
        var store = Store();
        new RepairQueue(store).Enqueue(new RepairTask("lost-on-restart", null, RepairReason.MissingReplica));

        var restarted = new RepairQueue(store);

        Assert.Equal(1, restarted.Rehydrate());
        Assert.Equal(1, restarted.Pending);
    }

    // ── Penalty stages (FR3, AC4) ─────────────────────────────────────────────

    [Fact]
    public void Penalty_Stage1And2_AddCopiesAsPendingReplicas_Stage4_AuditsToP0()
    {
        var store = Store();
        var options = Options.Create(new GatewayOptions { MinCopiesPerObject = 2 });
        var node = Node("p", "east", "");
        var registry = new Registry(node, Node("q", "west", ""));
        var queue = new RepairQueue(store);
        var source = new ManualPenaltySource();
        var responder = new PenaltyResponder(source, registry, store, queue, new PlacementEngine(registry, options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PenaltyResponder>.Instance);
        store.CommitObject(new ObjectRecord("b", "k", "cid1", 1, "x", DateTimeOffset.UtcNow, [node.Id, "http://q"]) { TargetCopies = 2 });

        Assert.Equal(1, responder.Apply(new NodePenaltyStageChanged(node.Id, 1, DateTimeOffset.UtcNow)));
        Assert.Equal(1, node.PenaltyStage);
        Assert.Equal(3, store.GetObject("b", "k")!.TargetCopies);
        Assert.Equal([(node.Id, 1, 1, false)], store.PendingReplicas("cid1"));
        Assert.Equal([0, 0, 1, 0], queue.PendingByTier);

        responder.Apply(new NodePenaltyStageChanged(node.Id, 2, DateTimeOffset.UtcNow));
        Assert.Equal(4, store.GetObject("b", "k")!.TargetCopies); // baseline + 2, not compounded
        Assert.Equal([0, 1, 0, 0], queue.PendingByTier);           // escalated to P1

        responder.Apply(new NodePenaltyStageChanged(node.Id, 4, DateTimeOffset.UtcNow));
        Assert.DoesNotContain(node.Id, store.GetObject("b", "k")!.NodeIds);
        Assert.Equal([1, 0, 0, 0], queue.PendingByTier);           // one copy left of four: P0
    }

    // ── Bandwidth budget (FR3.3, AC11) ────────────────────────────────────────

    [Fact]
    public async Task Budget_CapsTrafficPerNode_SharedAcrossTriggers()
    {
        var budget = new BandwidthBudget(Options.Create(new GatewayOptions
        {
            Redundancy = { BandwidthBudgetPercent = 25, DefaultNodeUploadBytesPerSecond = 400_000 },
        }));
        var node = Node("n", "east", "");
        Assert.Equal(100_000, budget.RateFor(node));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Two "trigger sources" interleaving on the same node: 300 KB at 100 KB/s, one second banked.
        await Task.WhenAll(
            Task.Run(async () => { for (var i = 0; i < 3; i++) await budget.AcquireAsync([node], 50_000, CancellationToken.None); }),
            Task.Run(async () => { for (var i = 0; i < 3; i++) await budget.AcquireAsync([node], 50_000, CancellationToken.None); }));

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.7), $"300 KB at 100 KB/s with 100 KB banked takes ~2 s, took {stopwatch.Elapsed}");
        Assert.Equal(300_000, budget.Spent(node.Id));

        node.PenaltyStage = 3;
        Assert.Equal(200_000, budget.RateFor(node)); // recovery budget (50 %)
    }

    // ── Challenge-response against real nodes (FR6.4, AC8, AC9) ───────────────

    [Fact]
    public async Task Challenge_PassesForAnIntactReplica_FailsForACorruptOne_AndCountsAgainstTheNode()
    {
        using var cluster = await TestCluster.StartAsync(2);
        var body = new byte[300 * 1024];
        Random.Shared.NextBytes(body);
        await cluster.PutAsync("challenge/key", body);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var record = store.GetObject(TestCluster.Bucket, "challenge/key")!;
        Assert.NotNull(record.MerkleRoot);

        foreach (var node in cluster.Connections)
        {
            Assert.Equal(ChallengeResult.Passed, await ChallengeScheduler.ChallengeAsync(node, record, TimeSpan.FromSeconds(10), CancellationToken.None));
        }

        // Corrupt node 0's copy on disk.
        var corrupted = cluster.Connections[0];
        var blobFile = Directory.EnumerateFiles(cluster.NodeDataDir(0), record.Cid + ".blob", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(blobFile);
        for (var i = 0; i < bytes.Length; i += 1000) bytes[i] ^= 0xFF;
        await File.WriteAllBytesAsync(blobFile, bytes);

        var result = await ChallengeScheduler.ChallengeAsync(corrupted, record, TimeSpan.FromSeconds(10), CancellationToken.None);
        var scheduler = cluster.Services.GetRequiredService<ChallengeScheduler>();
        scheduler.Record(corrupted, record, result);

        Assert.Equal(ChallengeResult.Failed, result);
        Assert.Equal(1, corrupted.ChallengeFailures);
        Assert.DoesNotContain(corrupted.Id, store.GetObject(TestCluster.Bucket, "challenge/key")!.NodeIds);
        Assert.Equal(0, PenaltyAssessment.RecommendedStage(corrupted.ChallengeFailures));
        Assert.Equal(1, PenaltyAssessment.RecommendedStage(3));
    }

    // ── Erasure coding end to end (FR5, AC7) ──────────────────────────────────

    private static Func<int, ProbeResponse> Regions(params string[] regions) =>
        i => new ProbeResponse { Healthy = true, AvailableStorageBytes = (1L << 30) - i, Region = regions[i] };

    [Fact]
    public async Task ErasureCoded_4Plus2_ReadsAfterLosingTwoShards_AndRebuildsALostShard()
    {
        var holes = new List<BlackHoleHandler>();
        using var cluster = await TestCluster.StartAsync(7, new Dictionary<string, string?> { ["Gateway:Redundancy:Mode"] = "Network" },
            handler: _ =>
            {
                var hole = new BlackHoleHandler { Enabled = false };
                holes.Add(hole);
                return hole;
            },
            probe: Regions("east", "west", "central", "east", "west", "central", "south"));
        using (var http = cluster.Signed())
        {
            var set = await http.PutAsync($"/v1/buckets/{TestCluster.Bucket}/scheme",
                new StringContent("""{"scheme":"rs:4+2"}""", System.Text.Encoding.UTF8, "application/json"));
            set.EnsureSuccessStatusCode();
        }

        var body = new byte[1_000_003]; // several stripes and a partial last one
        Random.Shared.NextBytes(body);
        await cluster.PutAsync("ec/key", body);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        var record = store.GetObject(TestCluster.Bucket, "ec/key")!;
        Assert.Equal("rs:4+2", record.Scheme);
        Assert.Empty(record.NodeIds);
        await PollAsync(() => store.Shards(TestCluster.Bucket, "ec/key").Count == 6);
        var shards = store.Shards(TestCluster.Bucket, "ec/key");
        Assert.Equal(6, shards.Select(s => s.NodeId).Distinct().Count()); // a shard is a placement unit

        Assert.Equal(body, await cluster.GetAsync("ec/key"));

        // Lose two data shards: the read decodes from parity.
        var lostNodes = shards.Where(s => s.Index is 0 or 2).Select(s => s.NodeId).ToHashSet();
        foreach (var (node, i) in cluster.Connections.Select((n, i) => (n, i)).Where(p => lostNodes.Contains(p.n.Id)))
        {
            holes[i].Enabled = true;
            node.MarkUnhealthy();
        }

        Assert.Equal(body, await cluster.GetAsync("ec/key"));
        using var range = await cluster.S3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = TestCluster.Bucket, Key = "ec/key", ByteRange = new ByteRange(250_000, 600_000),
        });
        using var buffer = new MemoryStream();
        await range.ResponseStream.CopyToAsync(buffer);
        Assert.Equal(body[250_000..600_001], buffer.ToArray());

        // Rebuild shard 0 onto the spare node, then read with only the rebuilt shard standing in.
        var rebuilt = await cluster.Services.GetRequiredService<ErasureObjects>().RebuildShardAsync(record, 0, null, CancellationToken.None);
        Assert.NotNull(rebuilt);
        Assert.DoesNotContain(rebuilt!.NodeId, lostNodes);
        Assert.Equal(body, await cluster.GetAsync("ec/key"));
    }

    private static GatewayMetadataStore Store()
    {
        var store = new GatewayMetadataStore(Options.Create(new GatewayOptions
        {
            MetadataDbPath = Path.Combine(Path.GetTempPath(), $"symposia-red-{Guid.NewGuid():N}", "m.db"),
        }));
        store.Initialize();
        return store;
    }

    private static async Task PollAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    }
}
