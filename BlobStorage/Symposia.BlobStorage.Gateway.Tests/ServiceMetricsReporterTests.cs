using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.History;
using Symposia.BlobStorage.Gateway.Rewards;
using Symposia.Network.Node.Rewards;
using Wire = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Blob Gateway reward reports (#221): factor measurement, the duration migration, the reporter's state machine.</summary>
public sealed class ServiceMetricsReporterTests : IDisposable
{
    private const string DevKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    private const string Node = "0x70997970C51812dc3A010C7d01b50e0d17dc79C8";
    private const string Distributor = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
    private const long EpochSeconds = 3600;
    private static readonly DateTimeOffset Genesis = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym221-" + Guid.NewGuid().ToString("N"));
    private readonly HistoryStore _store;
    private readonly MutableTime _time = new(Genesis);
    private readonly FakeChain _chain = new();
    private readonly FakeRelay _relay = new();
    private readonly HistoryRecorder _recorder;

    public ServiceMetricsReporterTests()
    {
        _store = new HistoryStore(Path.Combine(_dir, "gateway-history.db"));
        _recorder = new HistoryRecorder(_store, Options.Create(new GatewayOptions { ChainId = 31337 }), NullLogger<HistoryRecorder>.Instance, time: _time);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private ServiceMetricsReporter Reporter(Func<ulong, IMetricAttestor>? attestor = null, string distributor = Distributor) => new(_store, _recorder, _chain, _relay,
        attestor ?? (id => new LocalDevSelfSigner(DevKey, id)), Node, Options.Create(new ServiceMetricsOptions { RewardDistributorAddress = distributor }),
        NullLogger<ServiceMetricsReporter>.Instance, _time);

    private static HistoryEntry Row(DateTimeOffset at, string op, int status, long? size, long? durationMs) =>
        new(0, at, op, "b", "k", "s3", size, null, null, null, null, null, null, Guid.NewGuid().ToString("N"), status < 400 ? "Success" : "Error", status, durationMs);

    /// <summary>Epoch 0 traffic: 20 requests, one 5xx, durations 1..20 ms, three successful views.</summary>
    private void SeedEpoch0()
    {
        var rows = Enumerable.Range(1, 20).Select(i => Row(Genesis.AddSeconds(i), i <= 3 ? HistoryOperation.View : HistoryOperation.Insert,
            i == 20 ? 503 : 200, 1000 * i, i)).ToList();
        rows.Add(Row(Genesis.AddSeconds(30), HistoryOperation.View, 404, 5000, 1)); // a failed read serves nothing
        rows.Add(Row(Genesis.AddSeconds(EpochSeconds), HistoryOperation.View, 200, 9999, 999)); // epoch 1: excluded
        _store.Append(rows);
        for (var m = 0; m < 30; m++)
        {
            _store.RecordLiveness(Genesis.AddMinutes(m));
        }
    }

    /// <summary>Epoch 1, past the measuring grace for epoch 0.</summary>
    private void EnterEpoch1() => (_chain.Current, _time.Now) = (1, Genesis.AddSeconds(EpochSeconds) + ServiceMetricsReporter.MeasureGrace + TimeSpan.FromSeconds(1));

    [Fact]
    public void Aggregate_MatchesTheHistoryRows()
    {
        SeedEpoch0();

        var f = _store.Aggregate(Genesis.ToUnixTimeMilliseconds(), Genesis.AddSeconds(EpochSeconds).ToUnixTimeMilliseconds());

        Assert.Equal(21, f.Requests);
        Assert.Equal(10_000u * 20 / 21, f.SuccessBps); // one 503
        Assert.Equal(19, f.P95Ms); // nearest rank: ceil(0.95 * 21) = 20th of 1,1,2..20
        Assert.Equal(6000, f.BytesServed); // views of 1000 + 2000 + 3000; the 404 doesn't count
        Assert.Equal(5000u, f.UptimeBps); // 30 of 60 minutes
    }

    [Fact]
    public void Aggregate_NoTraffic_IsFullSuccessAndNoLatency()
    {
        var f = _store.Aggregate(0, 3_600_000);

        Assert.Equal((0L, 10_000u, 0L, 0L), (f.Requests, f.SuccessBps, f.P95Ms, f.BytesServed));
    }

    [Fact]
    public void Migration_AddsDurationToAnOldDatabase_AndIsIdempotent()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE object_history (id INTEGER PRIMARY KEY AUTOINCREMENT, ts_ms INTEGER NOT NULL, operation TEXT NOT NULL, bucket TEXT NOT NULL,
                    key TEXT NOT NULL, api TEXT NOT NULL, size_bytes INTEGER, etag TEXT, cid TEXT, client_ip TEXT, access_key_id TEXT, tenant_id TEXT,
                    user_agent TEXT, request_id TEXT NOT NULL, outcome TEXT NOT NULL, status INTEGER NOT NULL);
                INSERT INTO object_history (ts_ms, operation, bucket, key, api, request_id, outcome, status) VALUES (1000, 'view', 'b', 'k', 's3', 'r', 'Success', 200);
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var store = new HistoryStore(path);
        _ = new HistoryStore(path);
        store.Append([Row(DateTimeOffset.FromUnixTimeMilliseconds(2000), HistoryOperation.View, 200, 10, 7)]);

        var f = store.Aggregate(0, 3_600_000);
        Assert.Equal((2L, 7L), (f.Requests, f.P95Ms)); // the pre-migration row has no duration and is left out of p95
    }

    [Fact]
    public async Task FinishedEpoch_IsMeasuredSignedAndSubmitted()
    {
        SeedEpoch0();
        EnterEpoch1();

        await Reporter().TickAsync(default);

        var sent = Assert.Single(_relay.Sent);
        Assert.Equal((Node, (byte)8, 0UL), (sent.Node, sent.Service, sent.Epoch));
        Assert.Equal(["5000", (10_000 * 20 / 21).ToString(), "19", "6000"], sent.Values);
        Assert.Equal(5000u, sent.HeartbeatBps);
        var row = _store.GetEpochReport(Distributor, 0)!;
        Assert.Equal((EpochReportState.Submitted, "0xtx"), (row.State, row.TxHash));

        var signer = new LocalDevSelfSigner(DevKey, 31337);
        var report = new MetricReport(Distributor, Node, 8, 0, [.. sent.Values.Select(System.Numerics.BigInteger.Parse)], sent.HeartbeatBps);
        Assert.Equal(signer.Address, MetricReportDigest.Recover(report, sent.Signature), ignoreCase: true);
    }

    [Fact]
    public async Task SubmittedEpoch_IsNeverSentAgain()
    {
        SeedEpoch0();
        EnterEpoch1();
        var reporter = Reporter();

        await reporter.TickAsync(default);
        await reporter.TickAsync(default);
        await Reporter().TickAsync(default); // a restart

        Assert.Single(_relay.Sent);
    }

    [Fact]
    public async Task AlreadyOnChain_AfterACrash_IsMarkedSubmittedWithoutSending()
    {
        SeedEpoch0();
        EnterEpoch1();
        _relay.Next = new RelayOutcome(false, false, null, "timeout"); // the tx mined, but we never heard back
        await Reporter().TickAsync(default);
        _chain.Submitted = true;

        await Reporter().TickAsync(default);

        Assert.Single(_relay.Sent);
        Assert.Equal(EpochReportState.Submitted, _store.GetEpochReport(Distributor, 0)!.State);
    }

    [Fact]
    public async Task TransientFailure_RetriesWithTheSameSignature()
    {
        SeedEpoch0();
        EnterEpoch1();
        _relay.Next = new RelayOutcome(false, false, null, "502");
        var signs = 0;
        IMetricAttestor Counting(ulong id) => new CountingAttestor(new LocalDevSelfSigner(DevKey, id), () => signs++);

        await Reporter(Counting).TickAsync(default);
        await Reporter(Counting).TickAsync(default);

        Assert.Equal(2, _relay.Sent.Count);
        Assert.Equal(_relay.Sent[0].Signature, _relay.Sent[1].Signature);
        Assert.Equal(1, signs);
        var row = _store.GetEpochReport(Distributor, 0)!;
        Assert.Equal((EpochReportState.Submitted, 2), (row.State, row.Attempts));
    }

    [Fact]
    public async Task TerminalRefusal_StopsTheEpoch()
    {
        SeedEpoch0();
        EnterEpoch1();
        _relay.Next = new RelayOutcome(false, true, null, "InvalidSignature");
        var reporter = Reporter();

        await reporter.TickAsync(default);
        await reporter.TickAsync(default);

        Assert.Single(_relay.Sent);
        Assert.Equal((EpochReportState.Terminal, "InvalidSignature"), (_store.GetEpochReport(Distributor, 0)!.State, _store.GetEpochReport(Distributor, 0)!.LastError));
    }

    [Fact]
    public async Task WindowMissed_IsTerminalAndNothingIsSent()
    {
        SeedEpoch0();
        EnterEpoch1();
        _chain.WindowOpen = false;

        await Reporter().TickAsync(default);

        Assert.Empty(_relay.Sent);
        Assert.Equal((EpochReportState.Terminal, "FinalityWindowElapsed"), (_store.GetEpochReport(Distributor, 0)!.State, _store.GetEpochReport(Distributor, 0)!.LastError));
    }

    [Fact]
    public async Task NotActive_IsSkipped()
    {
        SeedEpoch0();
        EnterEpoch1();
        _chain.Active = false;

        await Reporter().TickAsync(default);

        Assert.Empty(_relay.Sent);
        Assert.Equal(EpochReportState.Skipped, _store.GetEpochReport(Distributor, 0)!.State);
    }

    [Fact]
    public async Task OffLocalDev_TheSelfSignerRefusesAndTheErrorEscapes()
    {
        _chain.ChainId = 8453;

        await Assert.ThrowsAsync<MetricSignerConfigurationException>(() => Reporter().TickAsync(default));
    }

    [Fact]
    public void OffLocalDev_GatewayRefusesToStart()
    {
        var ex = Assert.Throws<MetricSignerConfigurationException>(() => BlobGatewayApp.Create(new WebApplicationOptions(), b =>
        {
            b.Configuration["Gateway:ChainId"] = "84532";
            b.Configuration["Gateway:ServiceMetrics:Enabled"] = "true";
            b.Configuration["Gateway:MetadataDbPath"] = Path.Combine(_dir, "meta.db");
        }));
        Assert.Contains("only runs on LocalDev", ex.Message);
    }

    [Fact]
    public async Task UnmeasuredEpoch_HoldsTheSweep_UntilItIsMeasured()
    {
        // Just into epoch 1, inside the grace: epoch 0 isn't measured yet, so its rows are held.
        SeedEpoch0();
        _chain.Current = 1;
        _time.Now = Genesis.AddSeconds(EpochSeconds + 10);
        await Reporter().TickAsync(default);
        Assert.Equal(Genesis, _recorder.HoldFrom);

        _time.Now = Genesis.AddSeconds(EpochSeconds * 2 - 60); // epoch 0's rows are past the 1 h LocalDev retention
        Assert.Equal(0, _recorder.Sweep());

        await Reporter().TickAsync(default); // measures epoch 0, then holds only from epoch 1's start

        Assert.Equal(Genesis.AddSeconds(EpochSeconds), _recorder.HoldFrom);
        Assert.Equal(21, _recorder.Sweep());
    }

    [Fact]
    public void Restart_HoldsEverything_UntilTheReporterHasRun()
    {
        var recorder = new HistoryRecorder(_store, Options.Create(new GatewayOptions { ChainId = 31337, ServiceMetrics = { Enabled = true } }),
            NullLogger<HistoryRecorder>.Instance, time: _time);
        _store.Append([Row(Genesis, HistoryOperation.View, 200, 1, 1)]);
        _time.Now = Genesis.AddHours(2); // past retention, within MaxHold

        Assert.Equal(0, recorder.Sweep());
    }

    [Fact]
    public async Task EpochsMissedWhileBehind_AreStillReported()
    {
        // The gateway was down across a boundary: back at epoch 2 with epoch 0's window still open.
        SeedEpoch0();
        _chain.Current = 2;
        _time.Now = Genesis.AddSeconds(EpochSeconds * 2 + 60);

        await Reporter().TickAsync(default);

        Assert.Equal([0UL, 1UL], _relay.Sent.Select(r => r.Epoch).Order());
    }

    [Fact]
    public async Task ResetChain_UnderANewDistributor_ReportsAgain()
    {
        SeedEpoch0();
        EnterEpoch1();
        await Reporter().TickAsync(default);

        await Reporter(distributor: "0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512").TickAsync(default);

        Assert.Equal(2, _relay.Sent.Count);
    }

    [Fact]
    public async Task Skipped_IsRecheckedWhileTheWindowIsOpen()
    {
        SeedEpoch0();
        EnterEpoch1();
        _chain.Active = false;
        await Reporter().TickAsync(default);
        _chain.Active = true;

        await Reporter().TickAsync(default);

        Assert.Single(_relay.Sent);
        Assert.Equal(EpochReportState.Submitted, _store.GetEpochReport(Distributor, 0)!.State);
    }

    [Fact]
    public void Uptime_FullOnAnEpochThatDoesntStartOnAMinute()
    {
        var start = Genesis.AddSeconds(30);
        for (var t = start; t < start.AddSeconds(EpochSeconds); t = t.AddSeconds(15))
        {
            _store.RecordLiveness(t);
        }

        Assert.Equal(10_000u, _store.Aggregate(start.ToUnixTimeMilliseconds(), start.AddSeconds(EpochSeconds).ToUnixTimeMilliseconds()).UptimeBps);
    }

    [Fact]
    public void MultiObjectRequest_CountsOnceForSuccessAndLatency()
    {
        var rid = "multi";
        _store.Append([.. Enumerable.Range(0, 10).Select(i => Row(Genesis.AddSeconds(1), HistoryOperation.Delete, 503, null, 900) with { RequestId = rid, Key = "k" + i })]);
        _store.Append([Row(Genesis.AddSeconds(2), HistoryOperation.View, 200, 10, 5)]);

        var f = _store.Aggregate(Genesis.ToUnixTimeMilliseconds(), Genesis.AddSeconds(EpochSeconds).ToUnixTimeMilliseconds());

        Assert.Equal((2L, 5_000u), (f.Requests, f.SuccessBps));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Conflict, true)]
    [InlineData(System.Net.HttpStatusCode.BadRequest, true)]
    [InlineData(System.Net.HttpStatusCode.NotFound, false)]
    [InlineData(System.Net.HttpStatusCode.BadGateway, false)]
    public async Task Relay_OnlyRefusalsAreTerminal(System.Net.HttpStatusCode status, bool terminal)
    {
        var relay = new HttpServiceMetricsRelay(new HttpClient(new StatusHandler(status)) { BaseAddress = new Uri("http://relay") });

        var outcome = await relay.SubmitAsync(new Wire.Request(Node, 8, 0, ["1"], 0, "0x"), default);

        Assert.Equal((false, terminal), (outcome.Submitted, outcome.Terminal));
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("""{"error":"x"}""", System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public void Hold_IsBoundedSoAStuckReporterCantKeepHistoryForever()
    {
        _store.Append([Row(Genesis, HistoryOperation.View, 200, 1, 1)]);
        _recorder.HoldFrom = Genesis;
        _time.Now = Genesis + _recorder.Retention + HistoryRecorder.MaxHold + TimeSpan.FromMinutes(1);

        Assert.Equal(1, _recorder.Sweep());
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeChain : IServiceMetricsChain
    {
        public ulong ChainId { get; set; } = 31337;
        public ulong Current { get; set; }
        public bool WindowOpen { get; set; } = true;
        public bool Active { get; set; } = true;
        public bool Submitted { get; set; }

        public Task<ulong> ChainIdAsync(CancellationToken ct) => Task.FromResult(ChainId);
        public Task<ulong> CurrentEpochAsync(CancellationToken ct) => Task.FromResult(Current);
        public Task<long> EpochStartAsync(ulong epoch, CancellationToken ct) => Task.FromResult(Genesis.ToUnixTimeSeconds() + ((long)epoch * EpochSeconds));
        public Task<bool> AcceptsReportsAsync(ulong epoch, CancellationToken ct) => Task.FromResult(WindowOpen);
        public Task<bool> IsActiveAsync(string node, byte service, CancellationToken ct) => Task.FromResult(Active);
        public Task<bool> SubmittedAsync(ulong epoch, string node, byte service, CancellationToken ct) => Task.FromResult(Submitted);
    }

    private sealed class FakeRelay : IServiceMetricsRelay
    {
        public List<Wire.Request> Sent { get; } = [];

        /// <summary>The next outcome; after it, sends succeed.</summary>
        public RelayOutcome? Next { get; set; }

        public Task<RelayOutcome> SubmitAsync(Wire.Request request, CancellationToken ct)
        {
            Sent.Add(request);
            var outcome = Next ?? new RelayOutcome(true, false, "0xtx", null);
            Next = null;
            return Task.FromResult(outcome);
        }
    }

    private sealed class CountingAttestor(IMetricAttestor inner, Action onSign) : IMetricAttestor
    {
        public string Name => inner.Name;

        public Task<MetricAttestation> AttestAsync(MetricReport report, CancellationToken ct)
        {
            onSign();
            return inner.AttestAsync(report, ct);
        }
    }
}
