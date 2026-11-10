using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;
using Symposia.Network.Node.Telemetry;

namespace Symposia.Network.Node.Tests;

/// <summary>Issue #75: deterministic outlier rejection with fixed reference vectors (QA §2.5).</summary>
public sealed class RobustAggregatorTests
{
    [Fact]
    public void SevereLatencyOutlier_IsRejected()
    {
        ulong[] values = [50, 52, 48, 51, 49, 50_000];
        Assert.Equal([48UL, 49, 50, 51, 52], RobustAggregator.RejectOutliers(values));
        Assert.Equal(50UL, RobustAggregator.Aggregate(values));
    }

    [Theory]
    [InlineData(61, true)]  // 6745·51 = 343,995 ≤ 350,000
    [InlineData(62, false)] // 6745·52 = 350,740 > 350,000
    public void ModifiedZScoreBoundary_IsExact(ulong candidate, bool kept)
    {
        // median 10, MAD 10 either way, so only the candidate's own z-score decides.
        ulong[] values = [0, 0, 10, 10, 10, 20, 20, candidate];
        Assert.Equal(kept, RobustAggregator.RejectOutliers(values).Contains(candidate));
    }

    [Fact]
    public void ZeroMad_SkipsRejection() =>
        Assert.Equal(4, RobustAggregator.RejectOutliers([100, 100, 100, 100]).Count);

    [Fact]
    public void BelowMinimumSamples_SkipsRejection()
    {
        Assert.Equal(2, RobustAggregator.RejectOutliers([10, 1_000]).Count);
        Assert.Equal(505UL, RobustAggregator.Aggregate([10, 1_000]));
    }

    [Fact]
    public void EvenMedian_TruncatesTowardZero() => Assert.Equal(2UL, RobustAggregator.Median([1, 2, 3, 2, 3, 1]));

    [Fact]
    public void EmptySet_IsZero() => Assert.Equal(0UL, RobustAggregator.Aggregate([]));

    [Fact]
    public void ExtremeValues_DoNotOverflow() =>
        Assert.Equal(ulong.MaxValue, RobustAggregator.Median([ulong.MaxValue, ulong.MaxValue]));

    [Fact]
    public void Output_IsIndependentOfInputOrder()
    {
        var rng = new Random(75);
        var values = Enumerable.Range(0, 288).Select(_ => (ulong)rng.Next(40, 60)).Append(9_999UL).ToList();
        var expected = RobustAggregator.Aggregate(values);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(expected, RobustAggregator.Aggregate(values.OrderBy(_ => rng.Next()).ToList()));
        }
    }
}

/// <summary>Issue #75: signing, per-node epoch aggregation, Merkle batches, gossip and the emitter.</summary>
public sealed class TelemetryPipelineTests : IDisposable
{
    private const long Genesis = 1_700_000_000;
    private static readonly TelemetrySchedule Schedule = new(Genesis, 86_400);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym75-" + Guid.NewGuid().ToString("N"));
    private readonly NodeIdentity _node;
    private readonly NodeIdentity _verifier;
    private readonly NodeIdentity _other;
    private readonly EpochMetricAggregator _aggregator = new(Schedule, TestSupport.ChainId, TestSupport.Registry);

    public TelemetryPipelineTests()
    {
        Directory.CreateDirectory(_dir);
        _node = TestSupport.NewIdentity(_dir);
        _verifier = TestSupport.NewIdentity(_dir);
        _other = TestSupport.NewIdentity(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SignedTelemetry<HeartbeatSample> Beat(uint slot, uint latency = 50, ulong epoch = 0, NodeIdentity? by = null, long? at = null)
    {
        var signer = by ?? _node;
        var ts = at ?? Schedule.EpochStart(epoch) + slot * 300 + 7;
        return signer.SignHeartbeat(new HeartbeatSample(signer.NodeId, epoch, slot, ts, 1_000, 800, 50_000, 40_000, latency, 120, 5_000, 1_000));
    }

    private SignedTelemetry<Attestation> Attest(uint slot, bool reachable = true, uint latency = 55, NodeIdentity? by = null, ulong epoch = 0, long? at = null) =>
        (by ?? _verifier).SignAttestation(new Attestation((by ?? _verifier).NodeId, _node.NodeId, epoch, slot, at ?? Genesis + slot * 300, reachable, latency, 130));

    private AggregatedMetricRecord Aggregate(IEnumerable<SignedTelemetry<HeartbeatSample>> beats, IEnumerable<SignedTelemetry<Attestation>>? atts = null) =>
        _aggregator.Aggregate(_node.NodeId, 0, beats, atts ?? []);

    // --- signing ---

    [Fact]
    public void SignedHeartbeat_Verifies() =>
        Assert.True(TelemetrySigning.Verify(Beat(0), TestSupport.ChainId, TestSupport.Registry));

    [Fact]
    public void TamperedHeartbeat_FailsVerification()
    {
        var signed = Beat(0);
        var forged = signed with { Payload = signed.Payload with { ReadThroughputKBps = 999_999 } };
        Assert.False(TelemetrySigning.Verify(forged, TestSupport.ChainId, TestSupport.Registry));
    }

    [Fact]
    public void HeartbeatFromOtherNetwork_FailsVerification() =>
        Assert.False(TelemetrySigning.Verify(Beat(0), TestSupport.ChainId + 1, TestSupport.Registry));

    [Fact]
    public void MalformedSignature_FailsVerification() =>
        Assert.False(TelemetrySigning.Verify(Beat(0) with { Signature = "0x1234" }, TestSupport.ChainId, TestSupport.Registry));

    // --- aggregation (HC-*, OR-*, DH-*, EL-*) ---

    [Fact]
    public void FullCadence_IsFullCompliance()
    {
        var r = Aggregate(Enumerable.Range(0, 288).Select(i => Beat((uint)i)));
        Assert.Equal(288u, r.HeartbeatsReceived);
        Assert.Equal(10_000, r.HeartbeatComplianceBps);
        Assert.Equal(10_000u, r.SelfReported.UptimeBps);
        Assert.True(r.Eligible);
        Assert.Equal((ushort)0, r.FirstCheckpoint);
        Assert.Equal((ushort)23, r.LastCheckpoint);
    }

    [Fact]
    public void MissedHeartbeats_ReduceCompliance()
    {
        var r = Aggregate(Enumerable.Range(0, 288).Where(i => i % 10 != 0).Select(i => Beat((uint)i))); // 29 missed
        Assert.Equal(259u, r.HeartbeatsReceived);
        Assert.Equal(8_993, r.HeartbeatComplianceBps);
    }

    [Fact]
    public void NoHeartbeats_IsIneligible()
    {
        var r = Aggregate([]);
        Assert.False(r.Eligible);
        Assert.Equal(MetricValues.Empty, r.SelfReported);
    }

    [Fact]
    public void SingleSevereOutlier_IsExcluded()
    {
        var beats = Enumerable.Range(0, 100).Select(i => Beat((uint)i, latency: (uint)(48 + i % 5))).Append(Beat(100, latency: 50_000));
        Assert.Equal(50u, Aggregate(beats).SelfReported.LatencyMs);
    }

    [Fact]
    public void Replays_ForgeriesAndForeignSamples_AreDropped()
    {
        var good = Beat(0);
        var forged = Beat(1) with { Signature = Beat(2).Signature };
        var otherNode = Beat(3, by: _other);
        var otherEpoch = Beat(4, epoch: 1);
        var wrongSlotTime = Beat(5, at: Genesis + 6 * 300); // claims slot 5, timestamped in slot 6
        var r = Aggregate([good, good, forged, otherNode, otherEpoch, wrongSlotTime]);
        Assert.Equal(1u, r.HeartbeatsReceived);
    }

    [Fact]
    public void Aggregation_IsReproducibleAcrossInputOrder()
    {
        var rng = new Random(7);
        var beats = Enumerable.Range(0, 288).Select(i => Beat((uint)i, latency: (uint)rng.Next(30, 80))).ToList();
        var atts = Enumerable.Range(0, 20).Select(i => Attest((uint)i, latency: (uint)rng.Next(30, 90))).ToList();
        var expected = Aggregate(beats, atts);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(expected, Aggregate(beats.OrderBy(_ => rng.Next()), atts.OrderBy(_ => rng.Next())));
        }
    }

    [Fact]
    public void VerifierAndSelfReports_StaySeparate()
    {
        var beats = Enumerable.Range(0, 288).Select(i => Beat((uint)i, latency: 50));
        var atts = Enumerable.Range(0, 10).Select(i => Attest((uint)i, reachable: i >= 4, latency: 400)); // 60% reachable
        var r = Aggregate(beats, atts);
        Assert.Equal(10_000u, r.SelfReported.UptimeBps);
        Assert.Equal(50u, r.SelfReported.LatencyMs);
        Assert.Equal(6_000u, r.VerifierAttested.UptimeBps);
        Assert.Equal(400u, r.VerifierAttested.LatencyMs);
        Assert.Equal((ushort)10, r.AttestationCount);
    }

    [Fact]
    public void SelfAttestation_IsIgnored()
    {
        var self = _node.SignAttestation(new Attestation(_node.NodeId, _node.NodeId, 0, 0, Genesis, true, 1, 1));
        Assert.Equal((ushort)0, Aggregate([Beat(0)], [self]).AttestationCount);
    }

    [Fact]
    public void PartialEpoch_UsesProvidedExpectation()
    {
        var r = _aggregator.Aggregate(_node.NodeId, 0, Enumerable.Range(144, 144).Select(i => Beat((uint)i)), [], expectedHeartbeats: 144);
        Assert.Equal(10_000, r.HeartbeatComplianceBps);
    }

    // --- Merkle (CP-*) ---

    [Fact]
    public void HashPair_MatchesSolidityReferenceVector()
    {
        var a = Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("sampleA"));
        var b = Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("sampleB"));
        const string expected = "0x734dbd75d6eca4b078d4a4fd3cde12dd14ba7c599effa47670e8b4571bcf5a3a"; // cast keccak, sorted pair
        Assert.Equal(expected, TelemetryMerkle.HashPair(a, b).ToHex(true));
        Assert.Equal(expected, TelemetryMerkle.HashPair(b, a).ToHex(true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(12)]
    public void EveryLeaf_HasAValidProof(int count)
    {
        var batch = TelemetryMerkle.Build(Enumerable.Range(0, count).Select(i => Beat((uint)i)), [], TestSupport.ChainId, TestSupport.Registry);
        for (var i = 0; i < count; i++)
        {
            Assert.True(TelemetryMerkle.Verify(TelemetryMerkle.Proof(batch.Leaves, i), batch.Root, batch.Leaves[i]));
        }

        Assert.False(TelemetryMerkle.Verify(TelemetryMerkle.Proof(batch.Leaves, 0), batch.Root, new byte[32]));
    }

    [Fact]
    public void BatchRoot_IsIndependentOfArrivalOrder()
    {
        var beats = Enumerable.Range(0, 12).Select(i => Beat((uint)i, by: i % 2 == 0 ? _node : _other)).ToList();
        var atts = Enumerable.Range(0, 3).Select(i => Attest((uint)i)).ToList();
        var a = TelemetryMerkle.Build(beats, atts, TestSupport.ChainId, TestSupport.Registry);
        var b = TelemetryMerkle.Build(Enumerable.Reverse(beats), Enumerable.Reverse(atts), TestSupport.ChainId, TestSupport.Registry);
        Assert.Equal(a.Root, b.Root);
        Assert.Equal(15, a.Leaves.Count);
    }

    [Fact]
    public void EmptyBatch_IsRejected() =>
        Assert.Throws<ArgumentException>(() => TelemetryMerkle.Build([], [], TestSupport.ChainId, TestSupport.Registry));

    // --- gossip ---

    [Fact]
    public async Task Gossip_DeliversValidOnce_AndRejectsInvalid()
    {
        var gossip = new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry);
        var received = 0;
        gossip.HeartbeatReceived += _ => received++;
        var beat = Beat(0);
        Assert.True((await gossip.PublishAsync(beat)).Accepted);
        Assert.Equal(TelemetryRejection.Duplicate, (await gossip.PublishAsync(beat)).Rejection);
        Assert.Equal(TelemetryRejection.InvalidSignature, (await gossip.PublishAsync(Beat(1) with { Signature = beat.Signature })).Rejection);
        Assert.True((await gossip.PublishAsync(Attest(0))).Accepted);
        Assert.Equal(1, received);
        gossip.PruneBefore(1);
        Assert.Empty(gossip.Heartbeats);
    }

    [Fact]
    public async Task Gossip_KeepsAndForwardsOneConflictingCopy()
    {
        var gossip = new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry);
        var received = 0;
        gossip.HeartbeatReceived += _ => received++;
        Assert.True((await gossip.PublishAsync(Beat(3, latency: 50))).Accepted);
        Assert.True((await gossip.PublishAsync(Beat(3, latency: 9))).Accepted); // equivocation: forwarded as evidence
        Assert.Equal(TelemetryRejection.ReplayLimit, (await gossip.PublishAsync(Beat(3, latency: 7))).Rejection); // capped at two copies per key
        Assert.Equal(TelemetryRejection.Duplicate, (await gossip.PublishAsync(Beat(3, latency: 9))).Rejection); // exact copy: Duplicate wins
        Assert.Equal(2, received);
        Assert.Equal(2, gossip.Heartbeats.Count);
    }

    [Fact]
    public async Task Gossip_RejectsBackfilledAndFarEpochSamples()
    {
        var now = Schedule.EpochStart(1) + 10 * 300 + 5;
        var admission = new TelemetryAdmission(Schedule, new ManualTime(DateTimeOffset.FromUnixTimeSeconds(now)));
        var gossip = new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry, admission);
        Assert.True((await gossip.PublishAsync(Beat(10, epoch: 1))).Accepted);      // live
        Assert.Equal(TelemetryRejection.ClockSkew, (await gossip.PublishAsync(Beat(2, epoch: 1))).Rejection); // signed 40 minutes ago: backfill
        Assert.Equal(TelemetryRejection.EpochOutOfRange, (await gossip.PublishAsync(Beat(10, epoch: 3, at: now))).Rejection); // epoch too far ahead
        Assert.Single(gossip.Heartbeats);
    }

    [Fact]
    public async Task Gossip_ReportsWhyAnAttestationWasRejected()
    {
        var now = Schedule.EpochStart(1) + 10 * 300 + 5;
        var admission = new TelemetryAdmission(Schedule, new ManualTime(DateTimeOffset.FromUnixTimeSeconds(now)));
        var gossip = new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry, admission);
        var live = Attest(10, epoch: 1, at: now);
        Assert.True((await gossip.PublishAsync(live)).Accepted);
        Assert.Equal(TelemetryRejection.Duplicate, (await gossip.PublishAsync(live)).Rejection);
        Assert.Equal(TelemetryRejection.InvalidSignature, (await gossip.PublishAsync(Attest(11, epoch: 1, at: now) with { Signature = live.Signature })).Rejection);
        Assert.Equal(TelemetryRejection.ClockSkew, (await gossip.PublishAsync(Attest(12, epoch: 1, at: now + 3_600))).Rejection);
        Assert.Equal(TelemetryRejection.EpochOutOfRange, (await gossip.PublishAsync(Attest(10, epoch: 3, at: now))).Rejection);
    }

    [Theory]
    [InlineData(-400, "400 s behind")]
    [InlineData(400, "400 s ahead of")]
    public void Admission_DescribesClockSkewDirection(long offset, string expected)
    {
        var now = Schedule.EpochStart(1) + 10 * 300;
        var admission = new TelemetryAdmission(Schedule, new ManualTime(DateTimeOffset.FromUnixTimeSeconds(now)));

        var result = admission.Check(1, now + offset);

        Assert.Equal(TelemetryRejection.ClockSkew, result.Rejection);
        Assert.Contains(expected, result.Describe());
        Assert.Contains($"±{Schedule.CadenceSeconds + admission.ClockSkewSeconds} s", result.Describe());
    }

    [Fact]
    public void Aggregator_DropsEquivocatedSlot_RegardlessOfWhichCopyArrivedFirst()
    {
        var honest = Enumerable.Range(0, 10).Select(i => Beat((uint)i)).ToList();
        var conflict = Beat(4, latency: 9_999);
        var a = Aggregate(honest.Append(conflict));
        var b = Aggregate(new[] { conflict }.Concat(honest));
        Assert.Equal(a, b);
        Assert.Equal(9u, a.HeartbeatsReceived); // slot 4 counted as missed
    }

    // --- emitter ---

    [Fact]
    public async Task Emitter_SignsOncePerSlot()
    {
        var time = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(Genesis + 86_400 + 301));
        var gossip = new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry);
        var options = Microsoft.Extensions.Options.Options.Create(new TelemetryOptions { Enabled = true, GenesisUnixSeconds = Genesis });
        var emitter = new HeartbeatEmitter(_node, gossip, new LivenessOnlyMetricsSource(), options, time, NullLogger<HeartbeatEmitter>.Instance);

        var first = await emitter.EmitOnceAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(1UL, first.Payload.EpochIndex);
        Assert.Equal(1u, first.Payload.SampleIndex);
        Assert.Null(await emitter.EmitOnceAsync(CancellationToken.None));

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2u, (await emitter.EmitOnceAsync(CancellationToken.None))!.Payload.SampleIndex);
        Assert.Equal(2, gossip.Heartbeats.Count);
    }

    // Issue #181 FR1 / AC7: with a chain configured, nothing is stamped until the chain's schedule
    // arrives, and then epochs count from the chain's genesis, never the placeholder's.
    [Fact]
    public async Task Emitter_HoldsUntilTheChainScheduleSyncs_ThenUsesIt()
    {
        const long chainGenesis = 1_793_928_069;
        var time = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(chainGenesis + 3 * 3600 + 905));
        var gossip = new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry);
        var options = Microsoft.Extensions.Options.Options.Create(new TelemetryOptions { Enabled = true });
        var schedules = new TelemetryScheduleSource(options.Value.Schedule, requiresSync: true); // genesis 0 placeholder
        var diagnostics = new TelemetryDiagnostics();
        var emitter = new HeartbeatEmitter(_node, gossip, new LivenessOnlyMetricsSource(), options, time,
            NullLogger<HeartbeatEmitter>.Instance, schedules, diagnostics);

        Assert.Null(await emitter.EmitOnceAsync(CancellationToken.None));
        Assert.Empty(gossip.Heartbeats);

        schedules.Publish(new TelemetrySchedule(chainGenesis, 3600, 300, 4));
        var beat = await emitter.EmitOnceAsync(CancellationToken.None);

        Assert.NotNull(beat);
        Assert.Equal(3UL, beat.Payload.EpochIndex); // floor((t - G) / 3600)
        Assert.Equal(3u, beat.Payload.SampleIndex); // 905 s into the epoch, 300 s slots
        Assert.Equal(3UL, diagnostics.Current.LastAccepted!.Epoch);
    }

    // Issue #183: Diagnostics shows the specific admission rule, not a generic "wrong epoch or slot, or a replay".
    [Fact]
    public async Task Emitter_ReportsTheSpecificRejectionReason()
    {
        var time = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(Genesis + 86_400 + 301));
        var stale = new TelemetryAdmission(new TelemetrySchedule(Genesis - 10 * 86_400, 86_400), time); // disagrees with the emitter's epoch
        var gossip = new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry, stale);
        var options = Microsoft.Extensions.Options.Options.Create(new TelemetryOptions { Enabled = true, GenesisUnixSeconds = Genesis });
        var diagnostics = new TelemetryDiagnostics();
        var emitter = new HeartbeatEmitter(_node, gossip, new LivenessOnlyMetricsSource(), options, time,
            NullLogger<HeartbeatEmitter>.Instance, diagnostics: diagnostics);

        await emitter.EmitOnceAsync(CancellationToken.None);

        var rejected = diagnostics.Current.LastRejected!;
        Assert.Contains("epoch 1 is not within one of the current epoch 11", rejected.Reason);
        Assert.DoesNotContain("or a replay", rejected.Reason);
    }

    // #183 review: outside committee mode, admission follows the chain-synced schedule too, so a
    // heartbeat stamped under it isn't refused as EpochOutOfRange by the static placeholder.
    [Fact]
    public async Task RegisteredGossip_AdmitsUnderTheChainSyncedSchedule()
    {
        var chain = new TelemetrySchedule(Genesis - 10 * 86_400, 86_400);
        var now = chain.EpochStart(15) + 3 * 300 + 7;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Telemetry:Enabled"] = "true",
            ["Node:Telemetry:GenesisUnixSeconds"] = Genesis.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ManualTime(DateTimeOffset.FromUnixTimeSeconds(now)));
        services.Configure<NodeNetworkOptions>(o => (o.ChainId, o.NodeRegistryAddress) = (TestSupport.ChainId, TestSupport.Registry));
        services.AddTelemetryHeartbeats(config.GetSection("Node"));
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<TelemetryScheduleSource>().Publish(chain);

        var beat = _node.SignHeartbeat(new HeartbeatSample(_node.NodeId, 15, 3, now, 1, 1, 1, 1, 50, 120, 5_000, 1_000));

        Assert.True((await sp.GetRequiredService<ITelemetryGossip>().PublishAsync(beat)).Accepted);
    }

    [Fact]
    public void ScheduleSource_WithoutAChain_IsSyncedFromTheStart()
    {
        Assert.True(new TelemetryScheduleSource(new TelemetrySchedule(0, 86_400)).IsSynced);
        Assert.False(new TelemetryScheduleSource(new TelemetrySchedule(0, 86_400), requiresSync: true).IsSynced);
    }

    [Fact]
    public async Task Emitter_IsSilentBeforeGenesis()
    {
        var time = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(Genesis - 10));
        var options = Microsoft.Extensions.Options.Options.Create(new TelemetryOptions { Enabled = true, GenesisUnixSeconds = Genesis });
        var emitter = new HeartbeatEmitter(_node, new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry),
            new LivenessOnlyMetricsSource(), options, time, NullLogger<HeartbeatEmitter>.Instance);
        Assert.Null(await emitter.EmitOnceAsync(CancellationToken.None));
    }
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
