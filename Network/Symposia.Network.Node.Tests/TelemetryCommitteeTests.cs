using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.Hex.HexConvertors.Extensions;
using Symposia.Network.Node.Telemetry;
using Symposia.Network.Node.Telemetry.Committee;

namespace Symposia.Network.Node.Tests;

/// <summary>Issue #158: canonical batches and their tamper checks.</summary>
public sealed class TelemetryBatchCodecTests : IDisposable
{
    private readonly CommitteeHarness _h = new(members: 1);

    public void Dispose() => _h.Dispose();

    [Fact]
    public void RoundTripsAndIsOrderIndependent()
    {
        var beats = Enumerable.Range(0, 5).Select(i => _h.Beat(_h.Node, (uint)i)).ToList();
        var a = TelemetryBatchCodec.Encode(0, 0, beats, []);
        var b = TelemetryBatchCodec.Encode(0, 0, Enumerable.Reverse(beats), []);
        Assert.Equal(a, b);

        var decoded = TelemetryBatchCodec.Decode(a, CommitteeHarness.Schedule, TestSupport.ChainId, TestSupport.Registry);
        Assert.Equal(5u, decoded.SampleCount);
        Assert.Equal(TelemetryMerkle.Build(beats, [], TestSupport.ChainId, TestSupport.Registry).Root, decoded.MerkleRoot);
    }

    [Fact]
    public void RejectsSamplesOutsideTheWindowAndTampering()
    {
        // Slot 12 is window 1, not window 0.
        var wrongWindow = TelemetryBatchCodec.Encode(0, 0, [_h.Beat(_h.Node, 12)], []);
        Assert.Throws<InvalidDataException>(() => TelemetryBatchCodec.Decode(wrongWindow, CommitteeHarness.Schedule, TestSupport.ChainId, TestSupport.Registry));

        var good = TelemetryBatchCodec.Encode(0, 0, [_h.Beat(_h.Node, 1)], []);
        var forged = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(good).Replace("\"latencyMs\":50", "\"latencyMs\":5"));
        Assert.Throws<InvalidDataException>(() => TelemetryBatchCodec.Decode(forged, CommitteeHarness.Schedule, TestSupport.ChainId, TestSupport.Registry));

        var otherAddress = new byte[32];
        Assert.Throws<InvalidDataException>(() => TelemetryBatchCodec.DecodeCommitted(good, otherAddress,
            TelemetryBatchCodec.Decode(good, CommitteeHarness.Schedule, TestSupport.ChainId, TestSupport.Registry).MerkleRoot,
            CommitteeHarness.Schedule, TestSupport.ChainId, TestSupport.Registry));
    }
}

/// <summary>Issue #158 FR1: per-request authentication and forwarding on the HTTP gossip.</summary>
public sealed class TelemetryGossipTests : IDisposable
{
    private readonly CommitteeHarness _h = new(members: 3);

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task SamplesFloodToEveryPeerOnce()
    {
        var beat = _h.Beat(_h.Node, 3);
        Assert.True(await _h.Gossip[0].PublishAsync(beat));
        await _h.FlushAllAsync();

        Assert.All(_h.Gossip, g => Assert.Contains(beat, g.Store.Heartbeats));
        Assert.Equal(1, _h.Gossip[1].Metrics.Received.Value); // stored once, forwarded once, never looped
    }

    [Fact]
    public void RejectsUnknownStaleOrFloodingSenders()
    {
        var body = "{}"u8.ToArray();
        var now = _h.Time.GetUtcNow().ToUnixTimeSeconds();
        string Sign(NodeIdentity who, long at) => who.Sign(TelemetryGossipNode.RequestDigest(TestSupport.ChainId, at, body)).ToHex(true);

        var member = _h.Members[1];
        Assert.Null(_h.Gossip[0].Authenticate(member.NodeId, now.ToString(), Sign(member, now), body));
        Assert.Equal(401, _h.Gossip[0].Authenticate(_h.Node.NodeId, now.ToString(), Sign(_h.Node, now), body)); // not a recognized peer
        Assert.Equal(401, _h.Gossip[0].Authenticate(member.NodeId, (now - 120).ToString(), Sign(member, now - 120), body)); // stale
        Assert.Equal(401, _h.Gossip[0].Authenticate(member.NodeId, now.ToString(), Sign(_h.Members[2], now), body)); // wrong key

        for (var i = 0; i < 39; i++)
        {
            _h.Gossip[0].Authenticate(member.NodeId, now.ToString(), Sign(member, now), body);
        }

        Assert.Equal(429, _h.Gossip[0].Authenticate(member.NodeId, now.ToString(), Sign(member, now), body));
    }
}

/// <summary>Issue #158 FR2-FR4: proposal, endorsement, rotation, fallback and records, with three members (threshold 2).</summary>
public sealed class CommitteeProtocolTests : IDisposable
{
    private readonly CommitteeHarness _h = new(members: 3);

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ClosedWindowIsCheckpointedByThreshold()
    {
        var beats = await _h.EmitWindowAsync(0, gossipVia: 1);
        _h.Chain.Now = CommitteeHarness.WindowEnd(0) + CommitteeHarness.Settle;

        await _h.RoundsAsync(3);

        var committed = _h.Chain.Checkpoints[(0, 0)];
        Assert.Equal((uint)beats.Count, committed.SampleCount);
        var bytes = await _h.Blobs.GetAsync(committed.BlobAddress, default);
        Assert.Equal(committed.MerkleRoot,
            TelemetryBatchCodec.DecodeCommitted(bytes!, committed.BlobAddress, committed.MerkleRoot, CommitteeHarness.Schedule, TestSupport.ChainId, TestSupport.Registry).MerkleRoot);
        Assert.Equal(1, _h.Chain.CheckpointCommits); // racing submitters don't double-commit
    }

    [Fact]
    public async Task CensoringProposerIsRoutedAround()
    {
        await _h.EmitWindowAsync(0, gossipVia: 1);
        _h.Chain.Now = CommitteeHarness.WindowEnd(0) + CommitteeHarness.Settle;
        // The rotation-0 proposer cuts its batch, then a sample it doesn't hold reaches the other two.
        var proposer = _h.ProposerIndex(0, 0, rotation: 0);
        await _h.Workers[proposer].RunOnceAsync(default);
        var late = _h.Beat(_h.Other, 11);
        foreach (var i in Enumerable.Range(0, 3).Where(i => i != proposer))
        {
            await _h.Gossip[i].Store.PublishAsync(late);
        }

        await _h.RoundsAsync(2);
        Assert.False(_h.Chain.Checkpoints.ContainsKey((0, 0)));
        Assert.Equal(2, _h.Workers.Sum(w => w.Metrics.Refusals.Value));

        _h.Chain.Now += CommitteeHarness.Timeout; // rotation 1: the next proposer now holds the union
        await _h.RoundsAsync(3);

        var committed = _h.Chain.Checkpoints[(0, 0)];
        var batch = TelemetryBatchCodec.Decode((await _h.Blobs.GetAsync(committed.BlobAddress, default))!, CommitteeHarness.Schedule, TestSupport.ChainId, TestSupport.Registry);
        Assert.Contains(late, batch.Heartbeats);
    }

    [Fact]
    public async Task OfflineProposerIsReplacedAfterTheTimeout()
    {
        await _h.EmitWindowAsync(0, gossipVia: 1);
        var offline = _h.ProposerIndex(0, 0, rotation: 0);
        _h.Chain.Now = CommitteeHarness.WindowEnd(0) + CommitteeHarness.Settle;
        await _h.RoundsAsync(2, skip: offline);
        Assert.False(_h.Chain.Checkpoints.ContainsKey((0, 0)));

        _h.Chain.Now += CommitteeHarness.Timeout;
        await _h.RoundsAsync(3, skip: offline);
        Assert.True(_h.Chain.Checkpoints.ContainsKey((0, 0)));
    }

    [Fact]
    public async Task RecordsAreIdenticalAcrossMembersAndFinalize()
    {
        await _h.EmitWindowAsync(0, gossipVia: 0);
        await _h.EmitWindowAsync(1, gossipVia: 2);
        _h.Chain.Now = CommitteeHarness.WindowEnd(1) + CommitteeHarness.Settle;
        await _h.RoundsAsync(3);
        Assert.Equal(2, _h.Chain.Checkpoints.Count);

        _h.Chain.Now = _h.Chain.Deadline(0) + 1;
        var built = await Task.WhenAll(_h.Workers.Select(w => w.BuildRecordsAsync(CommitteeHarness.Schedule, 0, default)));
        var hashes = built.Select(r => TelemetryEip712.RecordsHash(r!).ToHex()).Distinct().ToList();
        Assert.Single(hashes);

        await _h.RoundsAsync(3);
        Assert.Contains(0UL, _h.Chain.Finalized);
        var record = Assert.Single(_h.Chain.Records);
        Assert.Equal(24u, record.HeartbeatsReceived); // 12 slots in each of two windows
        Assert.Equal(0, record.FirstCheckpoint);
        Assert.Equal(1, record.LastCheckpoint);
    }

    [Fact]
    public async Task NodeSelfCheckpointsWhenTheCommitteeIsSilentAndItCountsTowardRecords()
    {
        await _h.EmitWindowAsync(0, gossipVia: null);
        _h.Chain.Now = CommitteeHarness.WindowEnd(0) + CommitteeHarness.Grace;
        await _h.NodeFallback.RunOnceAsync(default);

        Assert.True(_h.Chain.SelfCheckpoints.ContainsKey((0, 0, _h.Node.NodeId.ToLowerInvariant())));
        Assert.False(_h.Chain.Checkpoints.ContainsKey((0, 0)));

        _h.Chain.Now = _h.Chain.Deadline(0) + 1;
        await _h.RoundsAsync(3);
        Assert.Equal(12u, Assert.Single(_h.Chain.Records).HeartbeatsReceived);
    }

    [Fact]
    public async Task EmptyEpochIsFinalizedWithOneEmptyChunk()
    {
        _h.Chain.Now = _h.Chain.Deadline(0) + 1;
        await _h.RoundsAsync(2);
        Assert.Contains(0UL, _h.Chain.Finalized);
        Assert.Empty(_h.Chain.Records);
    }
}

/// <summary>
/// Three committee members plus one ordinary node, wired through in-process gossip, a fake
/// registry that enforces the contract's signature rules, and a shared blob store.
/// </summary>
internal sealed class CommitteeHarness : IDisposable
{
    public const long Genesis = 1_700_000_000;
    public const long Settle = 600;
    public const long Timeout = 600;
    public const long Grace = 1_200;
    public static readonly TelemetrySchedule Schedule = new(Genesis, 86_400);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym158-" + Guid.NewGuid().ToString("N"));

    public CommitteeHarness(int members)
    {
        Directory.CreateDirectory(_dir);
        Members = [.. Enumerable.Range(0, members).Select(_ => TestSupport.NewIdentity(_dir)).OrderBy(m => m.NodeId.ToLowerInvariant(), StringComparer.Ordinal)];
        Node = TestSupport.NewIdentity(_dir);
        Other = TestSupport.NewIdentity(_dir);
        Time = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(Genesis + 60));
        Chain = new FakeTelemetryChain(Members.Select(m => m.NodeId).ToList(), members == 1 ? 1 : members / 2 + 1, Eip712);
        Relay = new FakeTelemetryRelay(Chain, Blobs, Eip712);
        var network = new InProcessNetwork();
        var factory = new FixedHttpClientFactory(new HttpClient(network));
        foreach (var member in Members)
        {
            var peers = new StaticPeers(Members.Where(m => m != member).Select(m => m.NodeId));
            var store = new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry);
            var gossip = new TelemetryGossipNode(store, peers, new NodeIdentityTelemetrySigner(member), Eip712, Time,
                Options.Create(new TelemetryGossipOptions()), TestSupport.ChainId);
            network.Nodes[member.NodeId.ToLowerInvariant()] = gossip;
            Gossip.Add(gossip);
            Workers.Add(new CommitteeWorker(Chain, Relay, Blobs, gossip, new NodeIdentityTelemetrySigner(member), Eip712, factory,
                Options.Create(new TelemetryCommitteeOptions { SettleDelaySeconds = (int)Settle, ProposalTimeoutSeconds = (int)Timeout }),
                TestSupport.ChainId, TestSupport.Registry, NullLogger<CommitteeWorker>.Instance));
        }

        NodeStore = new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry);
        NodeFallback = new SelfCheckpointWorker(Chain, Relay, NodeStore, new NodeIdentityTelemetrySigner(Node), Eip712, TestSupport.ChainId,
            TestSupport.Registry, NullLogger<SelfCheckpointWorker>.Instance);
        Factory = factory;
    }

    public TelemetryEip712 Eip712 { get; } = new(TestSupport.ChainId, "0x00000000000000000000000000000000000000aa");

    public List<NodeIdentity> Members { get; }

    public NodeIdentity Node { get; }

    /// <summary>A second ordinary node.</summary>
    public NodeIdentity Other { get; }

    public ManualTime Time { get; }

    public FakeTelemetryChain Chain { get; }

    public FakeTelemetryRelay Relay { get; }

    public CachingTelemetryBlobStore Blobs { get; } = new(null, null);

    public List<TelemetryGossipNode> Gossip { get; } = [];

    public List<CommitteeWorker> Workers { get; } = [];

    public InMemoryTelemetryGossip NodeStore { get; }

    public SelfCheckpointWorker NodeFallback { get; }

    private FixedHttpClientFactory Factory { get; }

    public static long WindowEnd(ushort k) => CommitteeWorker.WindowEnd(Schedule, 0, k);

    public SignedTelemetry<HeartbeatSample> Beat(NodeIdentity by, uint slot, ulong epoch = 0) =>
        by.SignHeartbeat(new HeartbeatSample(by.NodeId, epoch, slot, Schedule.EpochStart(epoch) + slot * 300 + 7, 1_000, 800, 50_000, 40_000, 50, 120, 5_000, 1_000));

    /// <summary>The node emits every slot of window <paramref name="k"/>; it reaches the committee through one member (or nobody).</summary>
    public async Task<List<SignedTelemetry<HeartbeatSample>>> EmitWindowAsync(ushort k, int? gossipVia)
    {
        var beats = Enumerable.Range(k * 12, 12).Select(s => Beat(Node, (uint)s)).ToList();
        foreach (var beat in beats)
        {
            await NodeStore.PublishAsync(beat);
            if (gossipVia is { } i)
            {
                await Gossip[i].PublishAsync(beat);
            }
        }

        await FlushAllAsync();
        return beats;
    }

    public int ProposerIndex(ulong epoch, ushort k, int rotation) => (int)((epoch * 24 + k + (ulong)rotation) % (ulong)Members.Count);

    public async Task FlushAllAsync()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var g in Gossip)
            {
                await g.FlushAsync(Factory.CreateClient(""), default);
            }
        }
    }

    public async Task RoundsAsync(int rounds, bool flush = true, int? skip = null)
    {
        for (var r = 0; r < rounds; r++)
        {
            for (var i = 0; i < Workers.Count; i++)
            {
                if (i != skip)
                {
                    await Workers[i].RunOnceAsync(default);
                }
            }

            if (flush)
            {
                await FlushAllAsync();
            }
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class StaticPeers(IEnumerable<string> addresses) : ITelemetryPeers
    {
        private readonly List<(string, string)> _peers = [.. addresses.Select(a => (a, $"http://{a.ToLowerInvariant()}/"))];

        public IReadOnlyList<(string Address, string Endpoint)> Peers() => _peers;
    }

    /// <summary>Routes gossip POSTs to the target node's handler, through the same authentication the endpoint applies.</summary>
    private sealed class InProcessNetwork : HttpMessageHandler
    {
        public Dictionary<string, TelemetryGossipNode> Nodes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var target = Nodes[request.RequestUri!.Host];
            var body = await request.Content!.ReadAsByteArrayAsync(ct);
            var rejected = target.Authenticate(request.Headers.GetValues(TelemetryGossipNode.SenderHeader).Single(),
                request.Headers.GetValues(TelemetryGossipNode.SentAtHeader).Single(), request.Headers.GetValues(TelemetryGossipNode.SignatureHeader).Single(), body);
            if (rejected is not null)
            {
                return new HttpResponseMessage((HttpStatusCode)rejected.Value);
            }

            await target.ReceiveAsync(System.Text.Json.JsonSerializer.Deserialize<TelemetryEnvelope>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!, ct);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

/// <summary>In-memory stand-in for TelemetryCheckpointRegistry + EpochController (24h epochs, 15-min finality, 30-min commit window).</summary>
internal sealed class FakeTelemetryChain(List<string> members, int threshold, TelemetryEip712 eip712) : ITelemetryChain
{
    public long Now { get; set; }

    public Dictionary<(ulong, ushort), CommittedCheckpoint> Checkpoints { get; } = [];

    public Dictionary<(ulong, ushort, string), CommittedCheckpoint> SelfCheckpoints { get; } = [];

    public HashSet<(ulong, int)> Chunks { get; } = [];

    public HashSet<ulong> Finalized { get; } = [];

    public List<AggregatedMetricInput> Records { get; } = [];

    public int CheckpointCommits { get; set; }

    public long Deadline(ulong epoch) => CommitteeHarness.Schedule.EpochStart(epoch + 1) + 900 + 1_800;

    public Task<long> NowAsync(CancellationToken ct) => Task.FromResult(Now);

    public Task<TelemetrySchedule> ScheduleAsync(CancellationToken ct) => Task.FromResult(CommitteeHarness.Schedule);

    public Task<(IReadOnlyList<string> Members, int Threshold)> CommitteeAsync(CancellationToken ct) =>
        Task.FromResult<(IReadOnlyList<string>, int)>((members, threshold));

    public Task<long> TelemetryDeadlineAsync(ulong epoch, CancellationToken ct) => Task.FromResult(Deadline(epoch));

    public Task<long> SelfCheckpointGraceAsync(CancellationToken ct) => Task.FromResult(CommitteeHarness.Grace);

    public Task<CommittedCheckpoint?> CheckpointAsync(ulong epoch, ushort checkpoint, CancellationToken ct) =>
        Task.FromResult(Checkpoints.GetValueOrDefault((epoch, checkpoint)));

    public Task<CommittedCheckpoint?> SelfCheckpointAsync(ulong epoch, ushort checkpoint, string node, CancellationToken ct) =>
        Task.FromResult(SelfCheckpoints.GetValueOrDefault((epoch, checkpoint, node.ToLowerInvariant())));

    public Task<IReadOnlyList<SelfCheckpointRef>> SelfCheckpointsAsync(ulong epoch, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SelfCheckpointRef>>([.. SelfCheckpoints.Where(s => s.Key.Item1 == epoch)
            .Select(s => new SelfCheckpointRef(epoch, s.Key.Item2, s.Key.Item3, s.Value.MerkleRoot, s.Value.BlobAddress))]);

    public Task<int> RecordsPerTxAsync(CancellationToken ct) => Task.FromResult(200);

    public Task<bool> RecordChunkWrittenAsync(ulong epoch, int chunk, CancellationToken ct) => Task.FromResult(Chunks.Contains((epoch, chunk)));

    public Task<bool> RecordsFinalizedAsync(ulong epoch, CancellationToken ct) => Task.FromResult(Finalized.Contains(epoch));

    /// <summary>The contract's threshold rule: distinct members, strictly ascending signer addresses, at least <c>threshold</c>.</summary>
    public void RequireThreshold(byte[] digest, IReadOnlyList<string> signatures)
    {
        var signers = signatures.Select(s => TelemetryEip712.Recover(digest, s)!.ToLowerInvariant()).ToList();
        Assert.Equal(signers.OrderBy(s => s, StringComparer.Ordinal).Distinct(), signers);
        Assert.All(signers, s => Assert.Contains(s, members.Select(m => m.ToLowerInvariant())));
        Assert.True(signers.Count >= threshold);
    }

    public TelemetryEip712 Eip712 => eip712;
}

internal sealed class FakeTelemetryRelay(FakeTelemetryChain chain, ITelemetryBlobStore blobs, TelemetryEip712 eip712) : ITelemetryRelay
{
    public Task CommitCheckpointAsync(ulong epoch, ushort checkpoint, byte[] merkleRoot, byte[] blobAddress, uint sampleCount,
        IReadOnlyList<string> signatures, CancellationToken ct)
    {
        Assert.True(chain.Now <= chain.Deadline(epoch));
        if (chain.Checkpoints.ContainsKey((epoch, checkpoint)))
        {
            return Task.CompletedTask; // "already committed" is success
        }

        chain.RequireThreshold(eip712.CheckpointDigest(epoch, checkpoint, merkleRoot, blobAddress, sampleCount), signatures);
        chain.Checkpoints[(epoch, checkpoint)] = new CommittedCheckpoint(merkleRoot, blobAddress, sampleCount);
        chain.CheckpointCommits++;
        return Task.CompletedTask;
    }

    public async Task SelfCheckpointAsync(string node, ulong epoch, ushort checkpoint, byte[] merkleRoot, byte[] blobAddress, uint sampleCount,
        string signature, byte[] batch, CancellationToken ct)
    {
        Assert.False(chain.Checkpoints.ContainsKey((epoch, checkpoint)));
        Assert.Equal(node.ToLowerInvariant(), TelemetryEip712.Recover(eip712.SelfCheckpointDigest(node, epoch, checkpoint, merkleRoot, blobAddress, sampleCount), signature)!.ToLowerInvariant());
        await blobs.PutAsync(blobAddress, batch, ct); // the Gateway stores relayed batches
        chain.SelfCheckpoints[(epoch, checkpoint, node.ToLowerInvariant())] = new CommittedCheckpoint(merkleRoot, blobAddress, sampleCount);
    }

    public Task SubmitRecordsAsync(ulong epoch, int chunkIndex, int chunkCount, IReadOnlyList<AggregatedMetricInput> records,
        IReadOnlyList<string> signatures, CancellationToken ct)
    {
        Assert.True(chain.Now > chain.Deadline(epoch));
        if (!chain.Chunks.Add((epoch, chunkIndex)))
        {
            return Task.CompletedTask;
        }

        chain.RequireThreshold(eip712.RecordsDigest(epoch, chunkIndex, chunkCount, records), signatures);
        chain.Records.AddRange(records);
        if (chain.Chunks.Count(c => c.Item1 == epoch) == chunkCount)
        {
            chain.Finalized.Add(epoch);
        }

        return Task.CompletedTask;
    }
}
