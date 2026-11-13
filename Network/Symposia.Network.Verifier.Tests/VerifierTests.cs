using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.Signer;
using Symposia.Network.Node;
using Symposia.Network.Node.Discovery;
using Symposia.Network.Node.Rewards;
using Wire = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire;

namespace Symposia.Network.Verifier.Tests;

/// <summary>
/// The Verifier role's engine (#225) against a fake gateway, chain and relay: factor arithmetic, authority modes,
/// discovery, monitor-health discards, bytes capping, and restart-safe submission.
/// </summary>
public sealed class VerifierTests : IDisposable
{
    private const string Distributor = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
    private const string Registry = "0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512";
    private const long EpochSeconds = 3600;
    private static readonly DateTimeOffset Genesis = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym225v-" + Guid.NewGuid().ToString("N"));
    private readonly EthECKey _signerKey = EthECKey.GenerateKey();
    private readonly EthECKey _self = EthECKey.GenerateKey();
    private readonly MutableTime _time = new(Genesis);
    private readonly FakeChain _chain = new();
    private readonly FakeRelay _relay = new();
    private readonly FakeNetwork _net;

    public VerifierTests()
    {
        Directory.CreateDirectory(_dir);
        _chain.Signer = _signerKey.GetPublicAddress();
        _net = new FakeNetwork(_time, _chain);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private VerifierOptions Options(ulong chainId = 31337, bool writeKey = true, EthECKey? key = null)
    {
        var path = Path.Combine(_dir, $"signer-{Guid.NewGuid():N}.key");
        if (writeKey)
        {
            LocalKeyMetricAttestor.Write(path, (key ?? _signerKey).GetPrivateKey());
        }

        return new VerifierOptions
        {
            ChainId = chainId, BlockchainGatewayUrl = "http://chain-gateway", NodeRegistryAddress = Registry, RewardDistributorAddress = Distributor,
            NodeAddress = _self.GetPublicAddress(), SignerKeyPath = path, DataDir = _dir, ExternalReferences = ["http://ref-a", "http://ref-b"],
            BytesServedFloor = 1000, IsPublicTarget = (_, _) => Task.FromResult(true),
        };
    }

    private VerifierEngine Engine(VerifierOptions options, IAttestationSigner? nodeSigner = null) => new(options, new VerifierStore(Path.Combine(_dir, "verifier.db")),
        _chain, _relay, new HttpClient(_net), NullLogger.Instance, _time, new Random(7), nodeSigner: nodeSigner);

    private sealed class KeySigner(EthECKey key) : IAttestationSigner
    {
        public string Address => key.GetPublicAddress();

        public string Sign(string contract, ulong chainId, ulong epoch, ulong nonce, IReadOnlyList<VerifierAttestationWire.ItemWire> items) =>
            VerifierAttestationWire.Sign(key, contract, chainId, epoch, nonce, items);
    }

    /// <summary>Ticks every 5 s through epoch 0, as the real loop does, then moves past the measuring grace and the figure wait.</summary>
    private async Task RunEpoch0Async(VerifierEngine engine, Func<DateTimeOffset, Task>? each = null)
    {
        for (var t = Genesis.AddSeconds(1); t < Genesis.AddSeconds(EpochSeconds + 10); t = t.AddSeconds(5))
        {
            _time.Now = t;
            _chain.Current = t < Genesis.AddSeconds(EpochSeconds) ? 0UL : 1UL;
            if (each is not null)
            {
                await each(t);
            }

            await TickAsync(engine, t);
        }

        _chain.Current = 1;
        _time.Now = Genesis.AddSeconds(EpochSeconds) + VerifierEngine.FigureWait + TimeSpan.FromSeconds(1);
    }

    /// <summary>One 5 s step of the real loops: health, discovery once a minute, probes.</summary>
    private static async Task TickAsync(VerifierEngine engine, DateTimeOffset t)
    {
        await engine.HealthTickAsync(default);
        if (t.ToUnixTimeSeconds() % 60 < 5 || t <= Genesis.AddSeconds(5))
        {
            await engine.DiscoveryTickAsync(default);
        }

        await engine.ProbeTickAsync(default);
    }

    private FakeGateway AddGateway(EthECKey? key = null, long bytes = 5000)
    {
        var gw = new FakeGateway(key ?? EthECKey.GenerateKey(), $"http://gw{_net.Gateways.Count}", bytes);
        _net.Gateways.Add(gw);
        _chain.Active.Add(new RegisteredNode(gw.Node, gw.Primary));
        return gw;
    }

    // ---------- arithmetic ----------

    [Fact]
    public void Factors_RoundDown_AndP95IsNearestRankOverSuccesses()
    {
        var samples = Enumerable.Range(1, 20).Select(i => new ProbeSample(8, "0x1", i, 0, "v", i == 20 ? ProbeOutcome.Timeout : i == 19 ? ProbeOutcome.RateLimited : ProbeOutcome.Ok, i)).ToList();

        var f = FactorMath.Compute(samples, 60);

        Assert.Equal((9500u, 9000u, 18L), (f.UptimeBps, f.SuccessBps, f.P95Ms)); // 19 up, 18 ok; ceil(0.95*18)=18th of 1..18
        Assert.True(f.LowCoverage); // 20 of 60 expected
    }

    [Theory]
    [InlineData(new long[] { 100, 120, 50_000 }, 1000, 1200)]
    [InlineData(new long[] { 100, 200 }, 0, 1500)]
    [InlineData(new long[] { 1 }, 1000, 1000)]
    [InlineData(new long[0], 1000, 1000)]
    public void Cap_IsTenTimesTheMedian_WithAFloor(long[] figures, long floor, long cap) =>
        Assert.Equal(cap, FactorMath.Cap(figures, floor));

    // ---------- discovery ----------

    [Fact]
    public void Directory_IsAcceptedOnlyForItsNode_Network_AndFreshness()
    {
        var key = EthECKey.GenerateKey();
        var d = ServiceDirectory.Sign(key, 31337, Registry, [new(8, "http://gw")], Genesis);

        Assert.Null(d.Problem(key.GetPublicAddress(), 31337, Registry, Genesis));
        Assert.NotNull(d.Problem(EthECKey.GenerateKey().GetPublicAddress(), 31337, Registry, Genesis));
        Assert.NotNull(d.Problem(key.GetPublicAddress(), 84532, Registry, Genesis));
        Assert.NotNull(d.Problem(key.GetPublicAddress(), 31337, Registry, Genesis + ServiceDirectory.Ttl + TimeSpan.FromMinutes(1)));
        Assert.NotNull(d.Problem(key.GetPublicAddress(), 31337, Registry, Genesis, minSeq: d.Seq + 1));
        Assert.NotNull((d with { Services = [new(8, "http://attacker")] }).Problem(key.GetPublicAddress(), 31337, Registry, Genesis));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("8.8.8.8", true)]
    public void Ssrf_PrivateAddressesAreRejected(string ip, bool isPublic) =>
        Assert.Equal(isPublic, TargetDirectory.IsPublic(IPAddress.Parse(ip)));

    // ---------- authority ----------

    [Fact]
    public async Task Authority_AttestsOnlyWithTheOnChainSignerKey()
    {
        Assert.Equal(VerifierMode.Attesting, (await new VerifierAuthority(_chain, Options(), NullLogger.Instance, _time).CheckAsync(true, default)).Mode);
        Assert.Equal(VerifierMode.ObserveOnly, (await new VerifierAuthority(_chain, Options(key: EthECKey.GenerateKey()), NullLogger.Instance, _time).CheckAsync(true, default)).Mode);
        Assert.Equal(VerifierMode.ObserveOnly, (await new VerifierAuthority(_chain, Options(writeKey: false), NullLogger.Instance, _time).CheckAsync(true, default)).Mode);

        _chain.FailSigner = true;
        Assert.Equal(VerifierMode.Unknown, (await new VerifierAuthority(_chain, Options(), NullLogger.Instance, _time).CheckAsync(true, default)).Mode);
    }

    [Fact]
    public async Task Authority_OffLocalDev_RefusesTheNodeKeyAsSigner()
    {
        _chain.ChainIdValue = 84532;
        _chain.Signer = _self.GetPublicAddress();
        var options = Options(chainId: 84532, key: _self);

        var (mode, reason) = await new VerifierAuthority(_chain, options, NullLogger.Instance, _time).CheckAsync(true, default);

        Assert.Equal(VerifierMode.ObserveOnly, mode);
        Assert.Contains("own key", reason);
    }

    [Fact]
    public async Task Authority_RefusesAGroupReadableKeyFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var options = Options();
        File.SetUnixFileMode(options.SignerKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var (mode, reason) = await new VerifierAuthority(_chain, options, NullLogger.Instance, _time).CheckAsync(true, default);

        Assert.Equal(VerifierMode.ObserveOnly, mode);
        Assert.Contains("readable by other users", reason);
    }

    // ---------- end to end ----------

    [Fact]
    public async Task SchedulerStall_DiscardsEveryMinuteItSpanned()
    {
        AddGateway();
        await using var engine = Engine(Options());
        _time.Now = Genesis.AddSeconds(50);
        await TickAsync(engine, _time.Now);
        _time.Now = Genesis.AddSeconds(150); // 100 s asleep: minutes 0, 1 and 2 are all suspect

        await engine.ProbeTickAsync(default);

        var store = new VerifierStore(Path.Combine(_dir, "verifier.db"));
        Assert.Equal(3, store.DiscardedMinutes(Genesis.ToUnixTimeMilliseconds(), Genesis.AddMinutes(3).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task SlowGateways_DontTripTheStallDetector()
    {
        // A pass that takes long because probes time out isn't a stall: the gap is measured from the pass's end.
        for (var i = 0; i < 5; i++)
        {
            AddGateway().ProbeDelay = TimeSpan.FromSeconds(20);
        }

        await using var engine = Engine(Options());
        _time.Now = Genesis.AddSeconds(1);
        await TickAsync(engine, _time.Now);
        _time.Now = Genesis.AddSeconds(6);
        await engine.ProbeTickAsync(default);

        Assert.Equal(0, engine.Snapshot().DiscardedMinutes);
    }

    [Fact]
    public async Task ManyTargets_ProbedConcurrently_AllRecorded()
    {
        for (var i = 0; i < 64; i++)
        {
            AddGateway();
        }

        await using var engine = Engine(Options());
        _time.Now = Genesis.AddSeconds(1);
        await TickAsync(engine, _time.Now);

        Assert.Equal(64, new VerifierStore(Path.Combine(_dir, "verifier.db")).TargetsIn(8, 0).Count);
        Assert.All(engine.Snapshot().Targets, t => Assert.Equal(ProbeOutcome.Ok, t.LastOutcome));
    }

    [Fact]
    public async Task GatewayDeactivatedAtTheBoundary_StillGetsItsFigure()
    {
        var gw = AddGateway(bytes: 7777);
        await using var engine = Engine(Options());
        await RunEpoch0Async(engine);
        _chain.Active.Clear();
        await engine.DiscoveryTickAsync(default);

        await engine.AttestTickAsync(default);

        Assert.Equal("7777", Assert.Single(_relay.Sent).Values[3]);
        Assert.Equal(gw.Node, _relay.Sent[0].Node, ignoreCase: true);
    }

    [Fact]
    public async Task ZeroHealthySamples_IsMeasuredOnce_AsNoSamples()
    {
        AddGateway();
        await using var engine = Engine(Options());
        _net.ReferencesDown = true; // every minute discarded
        await RunEpoch0Async(engine);
        _net.ReferencesDown = false;

        await engine.AttestTickAsync(default);
        var fetches = _net.FigureFetches;
        await engine.AttestTickAsync(default);

        Assert.Empty(_relay.Sent);
        Assert.Equal(fetches, _net.FigureFetches); // not re-examined every tick
        Assert.Equal("NoSamples", Assert.Single(engine.Snapshot().Reports).LastError);
    }

    [Fact]
    public void ReportState_NeverMovesBack()
    {
        var store = new VerifierStore(Path.Combine(_dir, "cas.db"));
        var row = new VerifierReport(Distributor, 8, "0x1", 0, [1, 2, 3, 4], 1, null, ReportFlags.None, ReportState.Submitted, null, null, "0xtx", null, 1, 1);
        store.SaveReport(row);

        store.SaveReport(row with { State = ReportState.Signed, TxHash = null });

        Assert.Equal((ReportState.Submitted, "0xtx"), (store.GetReport(Distributor, 8, "0x1", 0)!.State, store.GetReport(Distributor, 8, "0x1", 0)!.TxHash));
    }

    [Fact]
    public async Task HonestGateway_IsReportedWithFullScores_AndAValidSignature()
    {
        var gw = AddGateway();
        await using var engine = Engine(Options());
        await RunEpoch0Async(engine);

        await engine.AttestTickAsync(default);

        var sent = Assert.Single(_relay.Sent);
        Assert.Equal((gw.Node.ToLowerInvariant(), (byte)8, 0UL), (sent.Node.ToLowerInvariant(), sent.Service, sent.Epoch));
        Assert.Equal("10000", sent.Values[0]);
        Assert.Equal("10000", sent.Values[1]);
        Assert.Equal("5000", sent.Values[3]);
        var report = new MetricReport(Distributor, gw.Node, 8, 0, [.. sent.Values.Select(System.Numerics.BigInteger.Parse)], sent.HeartbeatBps);
        Assert.Equal(_signerKey.GetPublicAddress(), MetricReportDigest.Recover(report, sent.Signature), ignoreCase: true);
    }

    [Fact]
    public async Task BrokenCanary_LowersSuccess_UnreachableLowersUptime()
    {
        var broken = AddGateway();
        broken.ProbeStatus = HttpStatusCode.ServiceUnavailable;
        var wrong = AddGateway();
        wrong.WrongAnswer = true;
        await using var engine = Engine(Options());
        await RunEpoch0Async(engine);

        await engine.AttestTickAsync(default);

        var byNode = _relay.Sent.ToDictionary(r => r.Node, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(("0", "0"), (byNode[broken.Node].Values[0], byNode[broken.Node].Values[1])); // 5xx: down
        Assert.Equal(("10000", "0"), (byNode[wrong.Node].Values[0], byNode[wrong.Node].Values[1])); // answered, wrongly
    }

    [Fact]
    public async Task ObserveOnly_MeasuresButSendsNothing()
    {
        AddGateway();
        await using var engine = Engine(Options(key: EthECKey.GenerateKey()));
        await RunEpoch0Async(engine);

        await engine.AttestTickAsync(default);

        Assert.Empty(_relay.Sent);
        var snapshot = engine.Snapshot();
        Assert.Equal(VerifierMode.ObserveOnly, snapshot.Mode);
        Assert.Equal(ReportState.ObservedOnly, Assert.Single(snapshot.Reports).State);
    }

    [Fact]
    public async Task MonitorOutage_IsDiscardedForEveryGateway()
    {
        AddGateway();
        await using var engine = Engine(Options(chainId: 84532));
        _chain.ChainIdValue = 84532;
        await RunEpoch0Async(engine, t =>
        {
            var outage = t < Genesis.AddMinutes(10);
            _net.ReferencesDown = outage;
            _net.GatewaysDown = outage; // the monitor's own connectivity, not the gateway's fault
            return Task.CompletedTask;
        });
        await engine.AttestTickAsync(default);

        Assert.Equal("10000", Assert.Single(_relay.Sent).Values[0]);
    }

    [Fact]
    public async Task OwnNode_IsNeverMeasuredOffLocalDev()
    {
        AddGateway(_self);
        AddGateway();
        _chain.ChainIdValue = 84532;
        await using var engine = Engine(Options(chainId: 84532));
        await RunEpoch0Async(engine);

        await engine.AttestTickAsync(default);

        Assert.DoesNotContain(_relay.Sent, r => string.Equals(r.Node, _self.GetPublicAddress(), StringComparison.OrdinalIgnoreCase));
        Assert.Single(_relay.Sent);
    }

    [Fact]
    public async Task InflatedBytes_AreCapped_AndUnsignedBytesAreZero()
    {
        AddGateway(bytes: 1000);
        AddGateway(bytes: 1200);
        var inflated = AddGateway(bytes: 5_000_000);
        var unsigned = AddGateway(bytes: 1100);
        unsigned.SignFigure = false;
        await using var engine = Engine(Options());
        await RunEpoch0Async(engine);

        await engine.AttestTickAsync(default);

        var byNode = _relay.Sent.ToDictionary(r => r.Node, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("12000", byNode[inflated.Node].Values[3]); // 10 x median(1000, 1200, 5M) = 12000
        Assert.Equal("0", byNode[unsigned.Node].Values[3]);
    }

    [Fact]
    public async Task Restart_DoesNotSubmitTwice()
    {
        AddGateway();
        await using (var engine = Engine(Options()))
        {
            await RunEpoch0Async(engine);
            await engine.AttestTickAsync(default);
        }

        await using var restarted = Engine(Options());
        await restarted.AttestTickAsync(default);

        Assert.Single(_relay.Sent);
    }

    [Fact]
    public async Task TransientRelayFailure_IsRetried_TerminalIsNot()
    {
        AddGateway();
        AddGateway();
        await using var engine = Engine(Options());
        await RunEpoch0Async(engine);
        _relay.Script.Enqueue(new RelayOutcome(false, false, null, "502"));
        _relay.Script.Enqueue(new RelayOutcome(false, true, null, "InvalidSignature"));

        await engine.AttestTickAsync(default);
        await engine.AttestTickAsync(default);

        Assert.Equal(3, _relay.Sent.Count); // one retried once, one refused for good
    }

    [Fact]
    public async Task WindowClosed_LeavesReportsTerminal()
    {
        AddGateway();
        await using var engine = Engine(Options(key: EthECKey.GenerateKey()));
        await RunEpoch0Async(engine);
        await engine.AttestTickAsync(default);
        _chain.WindowOpen = false;

        await engine.AttestTickAsync(default);

        var report = Assert.Single(engine.Snapshot().Reports);
        Assert.Equal((ReportState.Terminal, "NotSigner"), (report.State, report.LastError));
    }

    [Fact]
    public async Task Undiscoverable_IsCountedAsDown()
    {
        var gw = AddGateway();
        gw.ServeDirectory = false;
        await using var engine = Engine(Options());
        await RunEpoch0Async(engine);

        await engine.AttestTickAsync(default);

        Assert.Equal("0", Assert.Single(_relay.Sent).Values[0]);
    }

    // ---------- Verifier duty (#227) ----------

    [Fact]
    public async Task Duty_AttestsEachAssignment_WithWhatWasMeasured_SignedByTheNodeKey()
    {
        var measured = AddGateway(bytes: 4321);
        var unmeasured = EthECKey.GenerateKey().GetPublicAddress(); // assigned, never probed: attested as down
        _chain.Assignments[0] = [(measured.Node, 1), (unmeasured, 0)];
        await using var engine = Engine(Options(), new KeySigner(_self));
        await engine.AttestTickAsync(default); // during epoch 0: joins the pool and records the assignments
        await RunEpoch0Async(engine);

        await engine.AttestTickAsync(default);

        Assert.Equal(2, _relay.Synced.Count); // once per epoch it ran in
        Assert.All(_relay.Synced, n => Assert.Equal(_self.GetPublicAddress(), n, ignoreCase: true));
        var batch = Assert.Single(_relay.Attested);
        Assert.Equal((0UL, 2), (batch.Epoch, batch.Items.Count));
        var forMeasured = batch.Items.Single(i => string.Equals(i.Target, measured.Node, StringComparison.OrdinalIgnoreCase));
        Assert.Equal((byte)1, forMeasured.Slot);
        Assert.Equal(["10000", "10000"], forMeasured.Values.Take(2));
        Assert.Equal("4321", forMeasured.Values[3]);
        Assert.Equal(["0", "0", "5000", "0"], batch.Items.Single(i => string.Equals(i.Target, unmeasured, StringComparison.OrdinalIgnoreCase)).Values); // down: p95 at the probe timeout
        Assert.True(batch.Nonce > 0);
        Assert.Equal(_self.GetPublicAddress(), VerifierAttestationWire.Recover(FakeChain.Contract, 31337, batch), ignoreCase: true);
    }

    [Fact]
    public async Task Duty_SendsOnce_AndWaitsForTheEpochsMeasurement()
    {
        AddGateway();
        _chain.Assignments[0] = [(_net.Gateways[0].Node, 0)];
        await using var engine = Engine(Options(), new KeySigner(_self));
        await engine.AttestTickAsync(default);
        await RunEpoch0Async(engine);
        _time.Now = Genesis.AddSeconds(EpochSeconds) + VerifierEngine.MeasureGrace + TimeSpan.FromSeconds(1); // before FigureWait

        await engine.AttestTickAsync(default);
        Assert.Empty(_relay.Attested);

        _time.Now = Genesis.AddSeconds(EpochSeconds) + VerifierEngine.FigureWait + TimeSpan.FromSeconds(1);
        await engine.AttestTickAsync(default);
        await engine.AttestTickAsync(default);
        Assert.Single(_relay.Attested);
        Assert.Equal(BatchState.Submitted, engine.Snapshot().Duty!.LastBatch!.State);
    }

    [Fact]
    public async Task Duty_StartedLate_StillLoadsAndAttestsTheFinishedEpoch()
    {
        AddGateway();
        _chain.Assignments[0] = [(_net.Gateways[0].Node, 2)];
        await using var engine = Engine(Options(), new KeySigner(_self));
        await RunEpoch0Async(engine); // no attest tick during epoch 0

        await engine.AttestTickAsync(default);

        Assert.Single(_relay.Attested);
    }

    [Fact]
    public async Task Duty_WindowClosed_IsTerminal()
    {
        AddGateway();
        _chain.Assignments[0] = [(_net.Gateways[0].Node, 0)];
        await using var engine = Engine(Options(), new KeySigner(_self));
        await engine.AttestTickAsync(default);
        await RunEpoch0Async(engine);
        _chain.WindowOpen = false;

        await engine.AttestTickAsync(default);

        Assert.Empty(_relay.Attested);
        Assert.Equal((BatchState.Terminal, "WindowClosed"), (engine.Snapshot().Duty!.LastBatch!.State, engine.Snapshot().Duty!.LastBatch!.LastError));
    }

    [Fact]
    public async Task Duty_VerifierOnlyHost_ProbesOnlyItsAssignments()
    {
        var mine = AddGateway();
        var other = AddGateway();
        _chain.Assignments[0] = [(mine.Node, 0)];
        await using var engine = Engine(Options(key: EthECKey.GenerateKey()), new KeySigner(_self)); // not the metric signer
        await engine.AttestTickAsync(default); // loads epoch 0's assignments
        await RunEpoch0Async(engine);

        var probed = new VerifierStore(Path.Combine(_dir, "verifier.db")).TargetsIn(8, 0).Select(t => t.Target).ToList();
        Assert.Contains(mine.Node.ToLowerInvariant(), probed);
        Assert.DoesNotContain(other.Node.ToLowerInvariant(), probed);
    }

    [Fact]
    public async Task Duty_FallbackFollowsTheEpochsFactorCount()
    {
        AddGateway();
        _chain.Assignments[0] = [(EthECKey.GenerateKey().GetPublicAddress(), 0)];
        _chain.Factors = 3;
        await using var engine = Engine(Options(), new KeySigner(_self));
        await engine.AttestTickAsync(default);
        await RunEpoch0Async(engine);

        await engine.AttestTickAsync(default);

        Assert.Equal(["0", "0", "0"], Assert.Single(Assert.Single(_relay.Attested).Items).Values);
    }

    [Fact]
    public async Task Duty_WithoutVerifierAssignmentsOnChain_DoesNothing()
    {
        AddGateway();
        _chain.Contract_ = "";
        await using var engine = Engine(Options(), new KeySigner(_self));
        await RunEpoch0Async(engine);

        await engine.AttestTickAsync(default);

        Assert.Empty(_relay.Synced);
        Assert.Empty(_relay.Attested);
    }

    // ---------- fakes ----------

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeChain : IVerifierChain
    {
        public ulong ChainIdValue { get; set; } = 31337;
        public ulong Current { get; set; }
        public string Signer { get; set; } = "";
        public bool FailSigner { get; set; }
        public bool WindowOpen { get; set; } = true;
        public List<RegisteredNode> Active { get; } = [];
        public const string Contract = "0x9fE46736679d2D9a65F0992F2272dE9f3c7fa6e0";
        public string Contract_ { get; set; } = Contract;
        public Dictionary<ulong, List<(string Target, byte Slot)>> Assignments { get; } = [];

        public Task<string> AssignmentsContractAsync(CancellationToken ct) => Task.FromResult(Contract_);
        public Task<bool> SeededAsync(ulong epoch, CancellationToken ct) => Task.FromResult(Assignments.ContainsKey(epoch));
        public int Factors { get; set; } = 4;
        public Task<int> FactorsInAsync(ulong epoch, CancellationToken ct) => Task.FromResult(Factors);
        public Task<IReadOnlyList<(string Target, byte Slot)>> AssignmentsOfAsync(ulong epoch, string verifier, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(string, byte)>>(Assignments.GetValueOrDefault(epoch) ?? []);

        public Task<ulong> ChainIdAsync(CancellationToken ct) => Task.FromResult(ChainIdValue);
        public Task<ulong> CurrentEpochAsync(CancellationToken ct) => Task.FromResult(Current);
        public Task<long> EpochStartAsync(ulong epoch, CancellationToken ct) => Task.FromResult(Genesis.ToUnixTimeSeconds() + ((long)epoch * EpochSeconds));
        public Task<bool> AcceptsReportsAsync(ulong epoch, CancellationToken ct) => Task.FromResult(WindowOpen);
        public Task<string> MetricSignerAsync(CancellationToken ct) => FailSigner ? throw new HttpRequestException("rpc down") : Task.FromResult(Signer);
        public Task<IReadOnlyList<RegisteredNode>> ActiveAsync(byte service, CancellationToken ct) => Task.FromResult<IReadOnlyList<RegisteredNode>>([.. Active]);
    }

    private sealed class FakeRelay : IReportRelay
    {
        public List<Wire.Request> Sent { get; } = [];

        public Queue<RelayOutcome> Script { get; } = new();
        public List<VerifierAttestationWire.Request> Attested { get; } = [];
        public List<string> Synced { get; } = [];

        public Task<RelayOutcome> AttestAsync(VerifierAttestationWire.Request request, CancellationToken ct)
        {
            lock (Attested)
            {
                Attested.Add(request);
            }

            return Task.FromResult(new RelayOutcome(true, false, "0xatt", null));
        }

        public Task<RelayOutcome> SyncAsync(string node, CancellationToken ct)
        {
            Synced.Add(node);
            return Task.FromResult(new RelayOutcome(true, false, null, null));
        }

        public Task<RelayOutcome> SubmitAsync(Wire.Request request, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(request);
                return Task.FromResult(Script.Count > 0 ? Script.Dequeue() : new RelayOutcome(true, false, "0xtx", null));
            }
        }
    }

    private sealed class FakeGateway(EthECKey key, string url, long bytes)
    {
        public EthECKey Key { get; } = key;
        public string Node => Key.GetPublicAddress();
        public string Primary { get; } = url.Replace("gw", "primary");
        public string Url { get; } = url;
        public long Bytes { get; } = bytes;
        public HttpStatusCode ProbeStatus { get; set; } = HttpStatusCode.OK;
        public bool WrongAnswer { get; set; }
        public bool SignFigure { get; set; } = true;
        public bool ServeDirectory { get; set; } = true;
        public TimeSpan ProbeDelay { get; set; }
    }

    /// <summary>Plays the Blockchain Gateway, the external references, and each gateway's directory, probe and figure.</summary>
    private sealed class FakeNetwork(MutableTime time, FakeChain chain) : HttpMessageHandler
    {
        public List<FakeGateway> Gateways { get; } = [];
        public bool ReferencesDown { get; set; }
        public bool GatewaysDown { get; set; }
        public int FigureFetches;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (origin.StartsWith("http://ref-") || origin == "http://chain-gateway")
            {
                return ReferencesDown ? throw new HttpRequestException("no route") : Json(HttpStatusCode.OK, new { });
            }

            if (Gateways.FirstOrDefault(g => g.Primary == origin) is { } byPrimary && uri.AbsolutePath == ServiceDirectory.Path)
            {
                return byPrimary.ServeDirectory
                    ? Json(HttpStatusCode.OK, ServiceDirectory.Sign(byPrimary.Key, chain.ChainIdValue, Registry, [new(8, byPrimary.Url)], time.Now))
                    : Json(HttpStatusCode.NotFound, new { });
            }

            if (Gateways.FirstOrDefault(g => g.Url == origin) is not { } gw)
            {
                throw new HttpRequestException("unknown host");
            }

            if (GatewaysDown)
            {
                throw new HttpRequestException("no route");
            }

            if (uri.AbsolutePath == ProbeChallenge.Path)
            {
                if (gw.ProbeDelay > TimeSpan.Zero)
                {
                    time.Now += gw.ProbeDelay; // a slow gateway: the probe pass takes this long on the clock
                    return Json(HttpStatusCode.GatewayTimeout, new { });
                }

                if (gw.ProbeStatus != HttpStatusCode.OK)
                {
                    return Json(gw.ProbeStatus, new { });
                }

                ProbeChallenge.TryParse(request.Headers.GetValues(ProbeChallenge.Header).Single(), out _, out var nonce, out _);
                var answer = gw.WrongAnswer ? "0xdead" : ProbeChallenge.Answer(nonce, ProbeChallenge.Canary(gw.Node));
                return Json(HttpStatusCode.OK, new ProbeChallenge.Response(answer));
            }

            if (uri.AbsolutePath.StartsWith(EpochReportSignature.PathPrefix))
            {
                Interlocked.Increment(ref FigureFetches);
                var epoch = ulong.Parse(uri.AbsolutePath[EpochReportSignature.PathPrefix.Length..]);
                var report = EpochReportSignature.Sign(gw.SignFigure ? gw.Key : EthECKey.GenerateKey(), chain.ChainIdValue, Distributor, epoch, gw.Bytes, 10) with { Node = gw.Node };
                return Json(HttpStatusCode.OK, report);
            }

            return Json(HttpStatusCode.NotFound, new { });
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode status, object body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(body) });
    }
}
