using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.History;
using Symposia.BlobStorage.Gateway.Rewards;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// A gateway measuring its own epochs (#221, measure-only since #225): factor arithmetic, the duration and
/// epoch_report → epoch_measurement migrations, the measuring grace and the history sweep hold.
/// </summary>
public sealed class EpochFactorRecorderTests : IDisposable
{
    private const string Distributor = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
    private const long EpochSeconds = 3600;
    private static readonly DateTimeOffset Genesis = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym221-" + Guid.NewGuid().ToString("N"));
    private readonly HistoryStore _store;
    private readonly MutableTime _time = new(Genesis);
    private readonly FakeChain _chain = new();
    private readonly HistoryRecorder _recorder;

    public EpochFactorRecorderTests()
    {
        _store = new HistoryStore(Path.Combine(_dir, "gateway-history.db"));
        _recorder = new HistoryRecorder(_store, Options.Create(new GatewayOptions { ChainId = 31337 }), NullLogger<HistoryRecorder>.Instance, time: _time);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private EpochFactorRecorder Recorder(string distributor = Distributor) => new(_store, _recorder, _chain,
        Options.Create(new ServiceMetricsOptions { RewardDistributorAddress = distributor }), NullLogger<EpochFactorRecorder>.Instance, _time);

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
    private void EnterEpoch1() => (_chain.Current, _time.Now) = (1, Genesis.AddSeconds(EpochSeconds) + EpochFactorRecorder.MeasureGrace + TimeSpan.FromSeconds(1));

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
    public void Migration_KeepsWhat221Measured_AndDropsItsSigningState()
    {
        var path = Path.Combine(_dir, "v221.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE epoch_report (distributor TEXT NOT NULL, epoch INTEGER NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
                    uptime_bps INTEGER NOT NULL, success_bps INTEGER NOT NULL, p95_ms INTEGER NOT NULL, bytes_served INTEGER NOT NULL, requests INTEGER NOT NULL,
                    state TEXT NOT NULL, signature TEXT, tx_hash TEXT, last_error TEXT, attempts INTEGER NOT NULL DEFAULT 0, updated_ms INTEGER NOT NULL,
                    PRIMARY KEY (distributor, epoch));
                INSERT INTO epoch_report VALUES ('0xd', 4, 0, 1, 9000, 9900, 12, 500, 7, 'Submitted', '0xsig', '0xtx', NULL, 1, 42);
                INSERT INTO epoch_report VALUES ('0xd', 5, 1, 2, 0, 0, 0, 0, 0, 'Terminal', NULL, NULL, 'FinalityWindowElapsed', 0, 43);
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var store = new HistoryStore(path);
        _ = new HistoryStore(path);

        var kept = store.GetMeasurement("0xd", 4)!;
        Assert.Equal((9000u, 500L, 42L), (kept.Factors.UptimeBps, kept.Factors.BytesServed, kept.MeasuredMs));
        Assert.Null(store.GetMeasurement("0xd", 5)); // never measured
    }

    [Fact]
    public async Task FinishedEpoch_IsMeasuredOnce_AfterTheGrace()
    {
        SeedEpoch0();
        _chain.Current = 1;
        _time.Now = Genesis.AddSeconds(EpochSeconds + 10);
        await Recorder().TickAsync(default);
        Assert.Null(_store.GetMeasurement(Distributor, 0));

        EnterEpoch1();
        await Recorder().TickAsync(default);
        _store.Append([Row(Genesis.AddSeconds(5), HistoryOperation.View, 200, 1_000_000, 1)]); // a straggler after measuring
        await Recorder().TickAsync(default);

        var m = _store.GetMeasurement(Distributor, 0)!;
        Assert.Equal((21L, 6000L), (m.Factors.Requests, m.Factors.BytesServed)); // immutable once measured
    }

    [Fact]
    public async Task UnmeasuredEpoch_HoldsTheSweep_UntilItIsMeasured()
    {
        SeedEpoch0();
        _chain.Current = 1;
        _time.Now = Genesis.AddSeconds(EpochSeconds + 10);
        await Recorder().TickAsync(default);
        Assert.Equal(Genesis, _recorder.HoldFrom);

        _time.Now = Genesis.AddSeconds(EpochSeconds * 2 - 60); // epoch 0's rows are past the 1 h LocalDev retention
        Assert.Equal(0, _recorder.Sweep());

        await Recorder().TickAsync(default);

        Assert.Equal(Genesis.AddSeconds(EpochSeconds), _recorder.HoldFrom);
        Assert.Equal(21, _recorder.Sweep());
    }

    [Fact]
    public void Restart_HoldsEverything_UntilTheRecorderHasRun()
    {
        var recorder = new HistoryRecorder(_store, Options.Create(new GatewayOptions { ChainId = 31337, ServiceMetrics = { Enabled = true } }),
            NullLogger<HistoryRecorder>.Instance, time: _time);
        _store.Append([Row(Genesis, HistoryOperation.View, 200, 1, 1)]);
        _time.Now = Genesis.AddHours(2);

        Assert.Equal(0, recorder.Sweep());
    }

    [Fact]
    public void Hold_IsBoundedSoAStuckRecorderCantKeepHistoryForever()
    {
        _store.Append([Row(Genesis, HistoryOperation.View, 200, 1, 1)]);
        _recorder.HoldFrom = Genesis;
        _time.Now = Genesis + _recorder.Retention + HistoryRecorder.MaxHold + TimeSpan.FromMinutes(1);

        Assert.Equal(1, _recorder.Sweep());
    }

    [Fact]
    public async Task EpochsMissedWhileBehind_AreStillMeasured()
    {
        SeedEpoch0();
        _chain.Current = 2;
        _time.Now = Genesis.AddSeconds(EpochSeconds * 2 + 60);

        await Recorder().TickAsync(default);

        Assert.NotNull(_store.GetMeasurement(Distributor, 0));
        Assert.NotNull(_store.GetMeasurement(Distributor, 1));
    }

    [Fact]
    public async Task ResetChain_UnderANewDistributor_MeasuresAgain()
    {
        SeedEpoch0();
        EnterEpoch1();
        await Recorder().TickAsync(default);

        await Recorder("0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512").TickAsync(default);

        Assert.NotNull(_store.GetMeasurement("0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512", 0));
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
        _store.Append([.. Enumerable.Range(0, 10).Select(i => Row(Genesis.AddSeconds(1), HistoryOperation.Delete, 503, null, 900) with { RequestId = "multi", Key = "k" + i })]);
        _store.Append([Row(Genesis.AddSeconds(2), HistoryOperation.View, 200, 10, 5)]);

        var f = _store.Aggregate(Genesis.ToUnixTimeMilliseconds(), Genesis.AddSeconds(EpochSeconds).ToUnixTimeMilliseconds());

        Assert.Equal((2L, 5_000u), (f.Requests, f.SuccessBps));
    }

    [Fact]
    public void ReservedSystemBuckets_AreNeverRecorded()
    {
        _recorder.Record(Row(Genesis, HistoryOperation.View, 200, 1, 1) with { Bucket = ".symposia-system" });
        _recorder.FlushNow();

        Assert.Empty(_store.Query(".symposia-system", "k", null, null, null, 0, 10));
    }

    [Fact]
    public void GatewayAssembly_HasNoMetricAttestor()
    {
        // #225 AC8: the gateway measures and serves its own figure but never signs a metric report.
        var types = typeof(BlobGatewayApp).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => typeof(Symposia.Network.Node.Rewards.IMetricAttestor).IsAssignableFrom(t));
        Assert.DoesNotContain(types.SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)),
            f => typeof(Symposia.Network.Node.Rewards.IMetricAttestor).IsAssignableFrom(f.FieldType));
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeChain : IServiceMetricsChain
    {
        public ulong Current { get; set; }

        public Task<ulong> ChainIdAsync(CancellationToken ct) => Task.FromResult(31337UL);
        public Task<ulong> CurrentEpochAsync(CancellationToken ct) => Task.FromResult(Current);
        public Task<long> EpochStartAsync(ulong epoch, CancellationToken ct) => Task.FromResult(Genesis.ToUnixTimeSeconds() + ((long)epoch * EpochSeconds));
        public Task<string> MetricSignerAsync(CancellationToken ct) => Task.FromResult("");
    }
}
