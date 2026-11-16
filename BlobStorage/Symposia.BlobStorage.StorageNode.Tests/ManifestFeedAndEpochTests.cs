using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.Protocol;
using Symposia.BlobStorage.StorageNode.Storage;
using Symposia.Network.Node;
using Symposia.Network.Node.Telemetry;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>
/// #12 Layers 2 and 3 on the node: the WatchManifest change feed (snapshot, backlog, live, resume, reset),
/// GetManifestProof against a submitted epoch, and the epoch root submitter (QA TC1.4, TC2.1, TC2.6, TC3.1, TC3.5).
/// </summary>
public sealed class ManifestFeedAndEpochTests : IClassFixture<StorageNodeIntegrationTests.NodeFactory>
{
    private readonly StorageNodeIntegrationTests.NodeFactory _factory;
    private readonly Protocol.StorageNode.StorageNodeClient _client;

    public ManifestFeedAndEpochTests(StorageNodeIntegrationTests.NodeFactory factory)
    {
        _factory = factory;
        var http = factory.CreateClient();
        _client = new Protocol.StorageNode.StorageNodeClient(GrpcChannel.ForAddress(http.BaseAddress!, new GrpcChannelOptions { HttpClient = http }));
    }

    private ManifestStore Manifest => _factory.Services.GetRequiredService<ManifestStore>();

    private async Task<string> WriteAsync(string key, string content, string tenant = "tenant-1")
    {
        using var call = _client.WriteBlob();
        await call.RequestStream.WriteAsync(new WriteBlobChunk { Header = new WriteBlobHeader { TenantId = tenant, Bucket = "feed", Key = key } });
        await call.RequestStream.WriteAsync(new WriteBlobChunk { Data = ByteString.CopyFromUtf8(content) });
        await call.RequestStream.CompleteAsync();
        return (await call.ResponseAsync).Cid;
    }

    /// <summary>Reads events until <paramref name="until"/> matches one (inclusive), within a few seconds.</summary>
    private static async Task<List<ManifestEvent>> ReadUntilAsync(AsyncServerStreamingCall<ManifestEvent> call, Func<ManifestEvent, bool> until)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<ManifestEvent>();
        while (await call.ResponseStream.MoveNext(timeout.Token))
        {
            events.Add(call.ResponseStream.Current);
            if (until(call.ResponseStream.Current)) break;
        }

        return events;
    }

    [Fact]
    public async Task WatchFromZero_SendsAResetSnapshot_ThenLiveChanges()
    {
        var existing = await WriteAsync("existing.txt", "written before the gateway subscribed " + Guid.NewGuid());
        using var cancel = new CancellationTokenSource();
        using var call = _client.WatchManifest(new WatchManifestRequest { AfterSeq = 0 }, cancellationToken: cancel.Token);

        var snapshot = await ReadUntilAsync(call, e => e.Kind == ManifestEvent.Types.Kind.CaughtUp);
        Assert.True(snapshot[0].Reset);
        Assert.Contains(snapshot, e => e.Kind == ManifestEvent.Types.Kind.Put && e.Cid == existing && e.Status == "Active");
        Assert.Equal(Manifest.CountActive(), snapshot[^1].ActiveCount);

        var live = await WriteAsync("live.txt", "written while subscribed " + Guid.NewGuid());
        var put = (await ReadUntilAsync(call, e => e.Cid == live))[^1];
        Assert.Equal(ManifestEvent.Types.Kind.Put, put.Kind);
        Assert.True(put.Seq > 0);

        await _client.DeleteBlobAsync(new DeleteBlobRequest { Cid = live });
        var removed = (await ReadUntilAsync(call, e => e.Cid == live))[^1];
        Assert.Equal(ManifestEvent.Types.Kind.Removed, removed.Kind);
        Assert.True(removed.Seq > put.Seq);
        cancel.Cancel();
    }

    [Fact]
    public async Task Resume_DeliversOnlyLaterEvents_AndAStaleCursorResets()
    {
        await WriteAsync("before.txt", "before the cursor " + Guid.NewGuid());
        var cursor = Manifest.HeadSeq();
        var after = await WriteAsync("after.txt", "after the cursor " + Guid.NewGuid());

        using (var call = _client.WatchManifest(new WatchManifestRequest { AfterSeq = cursor }))
        {
            var events = await ReadUntilAsync(call, e => e.Kind == ManifestEvent.Types.Kind.CaughtUp);
            Assert.DoesNotContain(events, e => e.Reset);
            Assert.Equal([after], events.Where(e => e.Kind == ManifestEvent.Types.Kind.Put).Select(e => e.Cid));
        }

        // A cursor from the future (the node's manifest was rebuilt) can't be trusted: resync.
        using var stale = _client.WatchManifest(new WatchManifestRequest { AfterSeq = cursor + 1_000_000 });
        var resynced = await ReadUntilAsync(stale, e => e.Kind == ManifestEvent.Types.Kind.CaughtUp);
        Assert.True(resynced[0].Reset);
    }

    [Fact]
    public async Task ACursorFromAnotherFeedIncarnation_Resets()
    {
        await WriteAsync("incarnation.txt", "seq spaces differ after a rebuild " + Guid.NewGuid());
        Assert.NotEmpty(Manifest.FeedId);

        using var same = _client.WatchManifest(new WatchManifestRequest { AfterSeq = Manifest.HeadSeq(), FeedId = Manifest.FeedId });
        var resumed = await ReadUntilAsync(same, e => e.Kind == ManifestEvent.Types.Kind.CaughtUp);
        Assert.DoesNotContain(resumed, e => e.Reset);
        Assert.All(resumed, e => Assert.Equal(Manifest.FeedId, e.FeedId));

        // A rebuilt manifest restarts seq at 1: a cursor that happens to be in range must still resync.
        using var other = _client.WatchManifest(new WatchManifestRequest { AfterSeq = 1, FeedId = "an-older-incarnation" });
        Assert.True((await ReadUntilAsync(other, e => e.Kind == ManifestEvent.Types.Kind.CaughtUp))[0].Reset);
    }

    [Fact]
    public async Task ManifestProof_ProvesAgainstTheSubmittedRoot_AndNotForAbsentCids()
    {
        var cid = await WriteAsync("proven.txt", "attested this epoch " + Guid.NewGuid(), tenant: "alice");
        var epoch = (ulong)Random.Shared.Next(1_000, 1_000_000);

        var unsubmitted = await _client.GetManifestProofAsync(new ManifestProofRequest { Cid = cid, Epoch = epoch });
        Assert.False(unsubmitted.Found); // frozen or not, nothing is answered for an epoch the chain doesn't hold

        var frozen = Manifest.FreezeEpoch(epoch);
        Manifest.MarkEpoch(epoch, "submitted", onChain: true);
        var proof = await _client.GetManifestProofAsync(new ManifestProofRequest { Cid = cid, Epoch = epoch });

        Assert.True(proof.Found);
        Assert.Equal(frozen.Root, proof.Root.ToByteArray());
        Assert.Equal("alice", proof.TenantId);
        Assert.True(ManifestMerkle.Verify(new ManifestLeaf(Cid.Parse(cid), proof.SizeBytes, proof.TenantId), proof.LeafIndex, proof.LeafCount,
            [.. proof.Siblings.Select(s => s.ToByteArray())], frozen.Root));

        var absent = await _client.GetManifestProofAsync(new ManifestProofRequest { Cid = new string('0', 64), Epoch = epoch });
        Assert.False(absent.Found);
        Assert.Equal(frozen.Root, absent.Root.ToByteArray());
    }

    // ── EpochRootSubmitter ────────────────────────────────────────────────────

    private sealed class ManualTime(long unixSeconds) : TimeProvider
    {
        public long Now { get; set; } = unixSeconds;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    }

    private sealed class FakeChain : IEpochRootClient
    {
        public List<(string Node, ulong Epoch, byte[] Root, byte[] Signature)> Submitted { get; } = [];

        public EpochRootSubmission Answer { get; set; } = EpochRootSubmission.Submitted;

        public Task<EpochRootSubmission> SubmitAsync(string node, ulong epoch, byte[] root, byte[] signature, CancellationToken ct = default)
        {
            Submitted.Add((node, epoch, root, signature));
            return Task.FromResult(Answer);
        }

        public Task<byte[]?> GetRootAsync(string node, ulong epoch, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

        public Task<(ulong Epoch, byte[] Root)?> GetLatestAsync(string node, CancellationToken ct = default) => Task.FromResult<(ulong, byte[])?>(null);
    }

    private static (EpochRootSubmitter Submitter, ManifestStore Manifest, NodeIdentity Identity) Submitter(string dir, FakeChain chain, ManualTime time)
    {
        var manifest = new ManifestStore(Options.Create(new StorageNodeOptions { ManifestDbPath = Path.Combine(dir, "manifest.db") }));
        manifest.Initialize();
        var network = Options.Create(new NodeNetworkOptions { NodeIdentityKeyPath = Path.Combine(dir, "node.key"), ChainId = 31337 });
        var identity = new NodeIdentity(network);
        identity.EnsureLoadedOrGenerated();
        var options = Options.Create(new StorageNodeOptions { EpochRoots = new EpochRootOptions { RegistryAddress = "0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512" } });
        var submitter = new EpochRootSubmitter(manifest, identity, chain, options, network, time, NullLogger<EpochRootSubmitter>.Instance,
            new TelemetryScheduleSource(new TelemetrySchedule(GenesisUnixSeconds: 1_000, EpochSeconds: 100)));
        return (submitter, manifest, identity);
    }

    [Fact]
    public async Task Submitter_AttestsTheFinishedEpochOnce_WithARecoverableSignature()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sym12-epoch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var chain = new FakeChain();
            var time = new ManualTime(1_050); // epoch 0: nothing has ended yet
            var (submitter, manifest, identity) = Submitter(dir, chain, time);
            var cid = Cid.FromHash(System.Security.Cryptography.SHA256.HashData("held"u8));
            manifest.Upsert(new BlobRecord(cid, 4, "alice", "b", "k", [], DateTimeOffset.UtcNow, null, BlobStatus.Active));

            Assert.Null(await submitter.RunOnceAsync(CancellationToken.None));

            time.Now = 1_130; // epoch 1 has begun: epoch 0 is finished
            var record = await submitter.RunOnceAsync(CancellationToken.None);
            await submitter.RunOnceAsync(CancellationToken.None); // already on-chain: no second submission

            var (node, epoch, root, signature) = Assert.Single(chain.Submitted);
            Assert.Equal((identity.NodeId, 0UL), (node, epoch));
            Assert.Equal(ManifestMerkle.Root([new ManifestLeaf(cid, 4, "alice")]), root);
            Assert.Equal("submitted", record!.Outcome);
            var digest = Eip712NodeRegistrySigner.SubmitRootDigest(node, epoch, root, "0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512", 31337);
            var recovered = EthECKey.RecoverFromSignature(EthECDSASignatureFactory.ExtractECDSASignature("0x" + Convert.ToHexString(signature)), digest);
            Assert.Equal(identity.NodeId, recovered.GetPublicAddress(), ignoreCase: true);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Submitter_RetriesARefusal_StopsOnAConflict_AndNeverSubmitsLate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sym12-epoch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var chain = new FakeChain { Answer = EpochRootSubmission.Rejected };
            var time = new ManualTime(1_150); // epoch 1: attest epoch 0
            var (submitter, manifest, _) = Submitter(dir, chain, time);

            await submitter.RunOnceAsync(CancellationToken.None);
            chain.Answer = EpochRootSubmission.Conflict;
            await submitter.RunOnceAsync(CancellationToken.None); // a refusal is retried
            await submitter.RunOnceAsync(CancellationToken.None); // a conflict is final

            Assert.Equal(2, chain.Submitted.Count);
            Assert.Equal("conflict", manifest.GetEpochRoot(0)!.Outcome);

            time.Now = 1_450; // epoch 4: epochs 1 and 2 passed while "down"; only epoch 3 is attested
            chain.Answer = EpochRootSubmission.Submitted;
            await submitter.RunOnceAsync(CancellationToken.None);
            Assert.Equal(3UL, chain.Submitted[^1].Epoch);
            Assert.Null(manifest.GetEpochRoot(1));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
