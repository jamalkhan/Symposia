using System.Net;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Quorum;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Write quorum enforcement and the consistency model (issue #7).</summary>
[Collection("GatewayIntegration")]
public sealed class WriteQuorumTests
{
    // ── The copy-count table (FR1, AC1) ───────────────────────────────────────

    [Theory]
    [InlineData(0, 4, 3)]
    [InlineData(1, 5, 3)]
    [InlineData(2, 6, 4)]
    [InlineData(3, 7, 4)]
    [InlineData(4, 8, 5)]
    [InlineData(5, 10, 6)]
    [InlineData(9, 18, 10)]
    public void Table_TotalCopiesAndQuorum(int regionCount, int total, int quorum)
    {
        var requirement = QuorumPolicy.Resolve(Enumerable.Range(0, regionCount).Select(i => $"r{i}").ToList());

        Assert.Equal(total, requirement.TotalCopies);
        Assert.Equal(quorum, requirement.WriteQuorum);
        Assert.Equal(total, requirement.PlacementPerRegion.Values.Sum() + requirement.OverflowCopies);
    }

    [Fact]
    public void Table_PlacementSplitsMatchTheSummaryTable()
    {
        Assert.Equal(3, QuorumPolicy.Resolve(["a"]).PlacementPerRegion["a"]);
        Assert.Equal(2, QuorumPolicy.Resolve(["a"]).OverflowCopies);
        Assert.Equal([3, 2], QuorumPolicy.Resolve(["a", "b"]).PlacementPerRegion.Values);
        Assert.Equal([2, 2, 2], QuorumPolicy.Resolve(["a", "b", "c"]).PlacementPerRegion.Values);
        Assert.Equal(0, QuorumPolicy.Resolve(["a", "b", "c", "d"]).OverflowCopies);
        Assert.Equal(3, QuorumPolicy.Resolve([]).MinDistinctRegions);
    }

    [Fact]
    public void Quorum_ExactlyQuorumAcksSucceed_OneFewerDoesNot()
    {
        foreach (var regions in new[] { Array.Empty<string>(), ["a"], ["a", "b"], ["a", "b", "c"], ["a", "b", "c", "d"] })
        {
            var requirement = QuorumPolicy.Resolve(regions);
            // Acks spread round-robin over the target regions, so every region minimum is met.
            var acks = Enumerable.Range(0, requirement.WriteQuorum).Select(i => regions.Length == 0 ? "" : regions[i % regions.Length]).ToList();

            Assert.True(requirement.IsSatisfiedBy(acks), $"{regions.Length} region(s): quorum acks");
            Assert.False(requirement.IsSatisfiedBy(acks.Skip(1)), $"{regions.Length} region(s): one fewer");
        }
    }

    [Fact]
    public void Quorum_OneRegion_NeedsTwoOfThreeInRegion()
    {
        var requirement = QuorumPolicy.Resolve(["east"]);

        Assert.False(requirement.IsSatisfiedBy(["east", "west", "central"])); // AC3: count alone isn't enough
        Assert.True(requirement.IsSatisfiedBy(["east", "east", "west"]));
    }

    [Fact]
    public void Quorum_TwoAndThreeRegions_NeedOneFromEachTarget()
    {
        var two = QuorumPolicy.Resolve(["a", "b"]);
        Assert.False(two.IsSatisfiedBy(["a", "a", "a", "x"]));
        Assert.True(two.IsSatisfiedBy(["a", "b", "x", "x"]));

        var three = QuorumPolicy.Resolve(["a", "b", "c"]);
        Assert.False(three.IsSatisfiedBy(["a", "b", "x", "x"]));
        Assert.True(three.IsSatisfiedBy(["a", "b", "c", "x"]));
    }

    // ── Last writer wins (FR11, AC11) ─────────────────────────────────────────

    [Fact]
    public void SameMillisecondCommits_ResolveToTheGreaterEtag_InEitherOrder()
    {
        var at = DateTimeOffset.Parse("2026-11-14T10:00:00.123Z");
        var low = Record("aaaa", at);
        var high = Record("ffff", at);

        var first = Store();
        first.CommitObject(low);
        Assert.True(first.CommitObject(high).Applied);
        Assert.Equal("ffff", first.GetObject("b", "k")!.Cid);

        var second = Store();
        second.CommitObject(high);
        Assert.False(second.CommitObject(low).Applied);
        Assert.Equal("ffff", second.GetObject("b", "k")!.Cid);
    }

    [Fact]
    public void LaterCommit_FromASlowClock_StillWins_AndTimestampsNeverRunBackwards() // review: Frank #1
    {
        var store = Store();
        var at = DateTimeOffset.Parse("2026-11-14T10:00:00.500Z");
        store.CommitObject(Record("ffff", at));

        // Serialized after the current version, so it is the later write whatever its gateway's clock says.
        var later = store.CommitObject(Record("0000", at.AddSeconds(-5)));

        Assert.True(later.Applied);
        Assert.Equal(at.AddMilliseconds(1), later.CommittedAt);
        Assert.Equal("0000", store.GetObject("b", "k")!.Cid);
    }

    [Fact]
    public void StaleMerkleRoot_FromAnOlderGatewaysOverwrite_IsIgnored() // review: Frank #2
    {
        var store = Store();
        store.CommitObject(Record("aaaa", DateTimeOffset.UtcNow) with { MerkleRoot = new string('1', 64) });
        Assert.NotNull(store.GetObject("b", "k")!.MerkleRoot);

        // An N-1 gateway's upsert changes the CID but knows nothing of merkle_root/merkle_cid.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionOf(store)))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE objects SET cid = 'bbbb' WHERE bucket = 'b' AND key = 'k'";
            cmd.ExecuteNonQuery();
        }

        Assert.Null(store.GetObject("b", "k")!.MerkleRoot);
    }

    private static string ConnectionOf(GatewayMetadataStore store) =>
        (string)typeof(GatewayMetadataStore).GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;

    private static ObjectRecord Record(string cid, DateTimeOffset at) => new("b", "k", cid, 1, "x", at, ["n1"]) { TargetCopies = 1 };

    private static GatewayMetadataStore Store()
    {
        var store = new GatewayMetadataStore(Options.Create(new GatewayOptions
        {
            MetadataDbPath = Path.Combine(Path.GetTempPath(), $"symposia-lww-{Guid.NewGuid():N}", "m.db"),
        }));
        store.Initialize();
        return store;
    }

    // ── Network-mode writes over a real cluster ───────────────────────────────

    private static Dictionary<string, string?> Network(int timeoutSeconds = 30) => new()
    {
        ["Gateway:Redundancy:Mode"] = "Network",
        ["Gateway:Redundancy:WriteQuorumTimeoutSeconds"] = timeoutSeconds.ToString(),
    };

    private static Func<int, ProbeResponse> Regions(params string[] regions) =>
        i => new ProbeResponse { Healthy = true, AvailableStorageBytes = (1L << 30) - i, Region = regions[i] };

    [Fact]
    public async Task GlobalBlob_AcksAtQuorum_AndTheRemainderLandsAfterwards()
    {
        // Four nodes over three regions; the fourth acks two seconds late.
        var slow = new DelayAckHandler(TimeSpan.FromSeconds(2));
        using var cluster = await TestCluster.StartAsync(4, Network(), handler: i => i == 3 ? slow : null,
            probe: Regions("east", "west", "central", "east"));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await cluster.PutAsync("q/global", "quorum"u8.ToArray());
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"acknowledged at quorum, not after the slow node ({stopwatch.Elapsed})");
        var meta = await MetaAsync(cluster, "q/global");
        Assert.Equal("quorum", meta.GetProperty("replication").GetString());
        Assert.Equal(3, meta.GetProperty("replicas").GetArrayLength());
        Assert.DoesNotContain(cluster.Connections[3].Id, meta.GetProperty("replicas").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("quorum"u8.ToArray(), await cluster.GetAsync("q/global")); // readable at once (AC6)

        await PollAsync(async () => (await MetaAsync(cluster, "q/global")).GetProperty("replication").GetString() == "full");
        meta = await MetaAsync(cluster, "q/global");
        Assert.Equal("full", meta.GetProperty("replication").GetString());
        Assert.Equal(4, meta.GetProperty("replicas").GetArrayLength());
        Assert.Equal(4, meta.GetProperty("targetCopies").GetInt32());
    }

    [Fact]
    public async Task OneAckShortOfQuorum_Fails503_CommitsNothing_AndQueuesTheLandedCopiesForGc()
    {
        using var cluster = await TestCluster.StartAsync(4, Network(), handler: i => i >= 2 ? new BlackHoleHandler() : null,
            probe: Regions("east", "west", "central", "east"));

        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => cluster.PutAsync("q/short", "short"u8.ToArray()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        var store = cluster.Services.GetRequiredService<GatewayMetadataStore>();
        Assert.Null(store.GetObject(TestCluster.Bucket, "q/short"));
        // Every target is queued, not only those that answered (one may have fsynced as it was cancelled); GC
        // checks references, so a node without the blob just gets an idempotent delete.
        Assert.Equal(4, store.GetPendingDeletions(10).Count);
    }

    [Fact]
    public async Task QuorumNotReachedWithinTheTimeout_Fails503_AndNothingIsReadable()
    {
        var slow = Enumerable.Range(0, 4).Select(_ => new DelayAckHandler(TimeSpan.FromSeconds(5))).ToList();
        using var cluster = await TestCluster.StartAsync(4, Network(timeoutSeconds: 1), handler: i => i >= 2 ? slow[i] : null,
            probe: Regions("east", "west", "central", "east"));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => cluster.PutAsync("q/timeout", "late"u8.ToArray()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"gave up at the timeout ({stopwatch.Elapsed})");
        await Assert.ThrowsAsync<AmazonS3Exception>(() => cluster.GetAsync("q/timeout"));
    }

    [Fact]
    public async Task OneRegionBucket_ThreeAcksButOnlyOneInRegion_IsNotQuorum()
    {
        // east ×3 (two of them can't take the write) + two overflow nodes elsewhere.
        using var cluster = await TestCluster.StartAsync(5, Network(), handler: i => i is 1 or 2 ? new BlackHoleHandler() : null,
            probe: Regions("east", "east", "east", "west", "central"));
        await SetRegionsAsync(cluster, "east");

        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => cluster.PutAsync("q/east", "x"u8.ToArray()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task OneRegionBucket_TwoInRegionPlusOneElsewhere_IsQuorum()
    {
        using var cluster = await TestCluster.StartAsync(5, Network(), handler: i => i is 2 or 4 ? new BlackHoleHandler() : null,
            probe: Regions("east", "east", "east", "west", "central"));
        await SetRegionsAsync(cluster, "east");

        await cluster.PutAsync("q/east-ok", "x"u8.ToArray());

        var meta = await MetaAsync(cluster, "q/east-ok");
        Assert.Equal(["east"], meta.GetProperty("regions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(5, meta.GetProperty("targetCopies").GetInt32());
    }

    [Fact]
    public async Task RegionThatCantHoldItsCopies_FailsClearly_WithoutWritingElsewhere()
    {
        var writes = 0;
        using var cluster = await TestCluster.StartAsync(5, Network(), handler: _ => new CountingHandler(() => Interlocked.Increment(ref writes)),
            probe: Regions("east", "east", "west", "west", "central"));
        await SetRegionsAsync(cluster, "east");

        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => cluster.PutAsync("q/no-room", "x"u8.ToArray()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal("InsufficientPlacementCapacity", ex.ErrorCode);
        Assert.Contains("east", ex.Message);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task BootstrapMode_RefusesRegionAssignments()
    {
        using var cluster = await TestCluster.StartAsync(1);
        using var http = cluster.Signed();

        var response = await http.PutAsync($"/v1/buckets/{TestCluster.Bucket}/regions",
            new StringContent("""{"regions":["east"]}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task MultipartComplete_InNetworkMode_IsImmediatelyVisible()
    {
        using var cluster = await TestCluster.StartAsync(4, Network(), probe: Regions("east", "west", "central", "east"));
        var init = await cluster.S3.InitiateMultipartUploadAsync(TestCluster.Bucket, "q/multi");
        var part = await cluster.S3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = TestCluster.Bucket, Key = "q/multi", UploadId = init.UploadId, PartNumber = 1,
            InputStream = new MemoryStream("only part"u8.ToArray()), UseChunkEncoding = false,
        });

        await cluster.S3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = TestCluster.Bucket, Key = "q/multi", UploadId = init.UploadId, PartETags = [new PartETag(1, part.ETag)],
        });

        var list = await cluster.S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = TestCluster.Bucket, Prefix = "q/multi" });
        Assert.Single(list.S3Objects);
        Assert.Equal("only part"u8.ToArray(), await cluster.GetAsync("q/multi"));
        Assert.Equal(4, (await MetaAsync(cluster, "q/multi")).GetProperty("targetCopies").GetInt32());
    }

    [Fact]
    public async Task UnderTargetSweep_FindsObjectsShortOfTheirOwnTarget()
    {
        var store = Store();
        store.CommitObject(new ObjectRecord("b", "full", "c1", 1, "x", DateTimeOffset.UtcNow, ["n1", "n2"]) { TargetCopies = 2 });
        store.CommitObject(new ObjectRecord("b", "short", "c2", 1, "x", DateTimeOffset.UtcNow, ["n1", "n2", "n3"]) { TargetCopies = 5 });
        store.CommitObject(new ObjectRecord("b", "shorter", "c3", 1, "x", DateTimeOffset.UtcNow, ["n1"]) { TargetCopies = 5 });

        var page = store.ListShortOfTarget(10);

        Assert.Equal(["short", "shorter"], page.Select(r => r.Key));
        Assert.Equal(ReplicationState.Quorum, page[0].Replication);
        Assert.Equal(["shorter"], store.ListShortOfTarget(10, (2, "b", "short")).Select(r => r.Key));
    }

    private static async Task SetRegionsAsync(TestCluster cluster, params string[] regions)
    {
        using var http = cluster.Signed();
        var response = await http.PutAsync($"/v1/buckets/{TestCluster.Bucket}/regions",
            new StringContent(JsonSerializer.Serialize(new { regions }), System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> MetaAsync(TestCluster cluster, string key)
    {
        using var http = cluster.Signed();
        var response = await http.GetAsync($"/v1/buckets/{TestCluster.Bucket}/objects/{key}?meta");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task PollAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100 && !await condition(); i++)
        {
            await Task.Delay(50);
        }
    }
}

/// <summary>Holds a node's WriteBlob answer (its durable ack) back by <paramref name="delay"/>.</summary>
internal sealed class DelayAckHandler(TimeSpan delay) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (request.RequestUri?.AbsolutePath.EndsWith("/WriteBlob", StringComparison.Ordinal) == true)
        {
            await Task.Delay(delay, ct);
        }

        return response;
    }
}
