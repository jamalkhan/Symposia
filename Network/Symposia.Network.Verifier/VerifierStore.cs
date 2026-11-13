using Microsoft.Data.Sqlite;

namespace Symposia.Network.Verifier;

/// <summary>What one probe saw (#225): stored as its ordinal, so the order is fixed.</summary>
public enum ProbeOutcome
{
    /// <summary>2xx with the right answer: up and successful.</summary>
    Ok = 0,

    /// <summary>2xx with the wrong answer: up, not successful.</summary>
    WrongAnswer = 1,

    /// <summary>A 5xx: down.</summary>
    ServerError = 2,

    /// <summary>Any other 4xx: up, not successful.</summary>
    OtherStatus = 3,

    Timeout = 4,

    ConnectFailed = 5,

    /// <summary>429: the gateway answered, so up, but not successful.</summary>
    RateLimited = 6,

    /// <summary>No authenticated URL for the target: counted as down.</summary>
    Undiscoverable = 7,
}

public static class ProbeOutcomes
{
    public static bool IsUp(ProbeOutcome o) => o is ProbeOutcome.Ok or ProbeOutcome.WrongAnswer or ProbeOutcome.OtherStatus or ProbeOutcome.RateLimited;
}

public sealed record ProbeSample(byte Service, string Target, long TsMs, ulong Epoch, string Vantage, ProbeOutcome Outcome, long LatencyMs);

/// <summary>Where a (target, epoch) report is. Terminal states are never revisited.</summary>
public enum ReportState
{
    Measured = 0,
    ObservedOnly = 1,
    Signed = 2,
    Submitted = 3,
    Terminal = 4,
}

[Flags]
public enum ReportFlags
{
    None = 0,
    BytesCapped = 1,
    BytesMissing = 2,
    BytesBadSignature = 4,
    LowCoverage = 8,
}

/// <summary>An epoch's attestation batch (#227): Pending until sent; Submitted or Terminal are final.</summary>
public enum BatchState
{
    Pending = 0,
    Retrying = 1,
    Submitted = 2,
    Terminal = 3,
}

public sealed record VerifierBatch(ulong Epoch, BatchState State, int Items, string? TxHash, string? LastError, int Attempts);

public sealed record VerifierReport(
    string Distributor, byte Service, string Target, ulong Epoch, IReadOnlyList<long> Values, uint HeartbeatBps, long? BytesClaimed,
    ReportFlags Flags, ReportState State, string? Signer, string? Signature, string? TxHash, string? LastError, int Attempts, long UpdatedMs);

/// <summary>
/// The Verifier's own SQLite store (#225 arch §3), WAL mode. Every query is a primary-key or named-index lookup:
/// samples by (service, target, ts) range, minutes by PK, the open-report work queue by the partial
/// <c>idx_report_open</c>, and the sweep by <c>idx_sample_epoch</c>. Retention is by epoch, not wall time.
/// </summary>
public sealed class VerifierStore
{
    private readonly string _connectionString;

    public VerifierStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS monitor_minute (
                minute_ms INTEGER PRIMARY KEY,
                ok        INTEGER NOT NULL,
                detail    TEXT
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS probe_sample (
                service    INTEGER NOT NULL,
                target     TEXT    NOT NULL,
                ts_ms      INTEGER NOT NULL,
                epoch      INTEGER NOT NULL,
                vantage    TEXT    NOT NULL,
                outcome    INTEGER NOT NULL,
                latency_ms INTEGER NOT NULL,
                PRIMARY KEY (service, target, ts_ms, vantage)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS idx_sample_epoch ON probe_sample (epoch);
            CREATE TABLE IF NOT EXISTS verifier_report (
                distributor   TEXT    NOT NULL,
                service       INTEGER NOT NULL,
                target        TEXT    NOT NULL,
                epoch         INTEGER NOT NULL,
                values_csv    TEXT    NOT NULL,
                heartbeat_bps INTEGER NOT NULL,
                bytes_claimed INTEGER,
                flags         INTEGER NOT NULL DEFAULT 0,
                state         INTEGER NOT NULL,
                signer        TEXT,
                signature     TEXT,
                tx_hash       TEXT,
                last_error    TEXT,
                attempts      INTEGER NOT NULL DEFAULT 0,
                updated_ms    INTEGER NOT NULL,
                PRIMARY KEY (distributor, service, target, epoch)
            ) WITHOUT ROWID;
            DROP INDEX IF EXISTS idx_report_open;
            CREATE INDEX IF NOT EXISTS idx_report_open_by_epoch ON verifier_report (distributor, epoch) WHERE state IN (0, 1, 2);
            -- Where each target was probed in each epoch, so a gateway deactivated since still gets its figure fetched.
            CREATE TABLE IF NOT EXISTS verifier_target (
                service INTEGER NOT NULL,
                target  TEXT    NOT NULL,
                epoch   INTEGER NOT NULL,
                url     TEXT    NOT NULL,
                PRIMARY KEY (service, target, epoch)
            ) WITHOUT ROWID;
            -- #227: this Verifier's on-chain assignments per epoch, and each epoch's attestation batch state.
            CREATE TABLE IF NOT EXISTS verifier_assignment (
                epoch  INTEGER NOT NULL,
                target TEXT    NOT NULL,
                slot   INTEGER NOT NULL,
                PRIMARY KEY (epoch, target)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS verifier_batch (
                epoch      INTEGER PRIMARY KEY,
                state      INTEGER NOT NULL,
                items      INTEGER NOT NULL,
                tx_hash    TEXT,
                last_error TEXT,
                attempts   INTEGER NOT NULL DEFAULT 0,
                updated_ms INTEGER NOT NULL
            ) WITHOUT ROWID;
            -- The highest directory seq seen per node: a captured older directory can't roll a URL back.
            CREATE TABLE IF NOT EXISTS directory_seen (
                node TEXT PRIMARY KEY,
                seq  INTEGER NOT NULL
            ) WITHOUT ROWID;
            """;
        cmd.ExecuteNonQuery();
    }

    public void RecordMinute(long minuteMs, bool ok, string? detail)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // A minute is healthy only if every check in it passed: a later failure downgrades it, never the reverse.
        cmd.CommandText = """
            INSERT INTO monitor_minute (minute_ms, ok, detail) VALUES ($m, $ok, $d)
            ON CONFLICT (minute_ms) DO UPDATE SET ok = MIN(ok, $ok), detail = CASE WHEN $ok = 0 THEN $d ELSE detail END
            """;
        cmd.Parameters.AddWithValue("$m", minuteMs);
        cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
        cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Healthy minutes in [<paramref name="fromMs"/>, <paramref name="toMs"/>): the expected-sample denominator.</summary>
    public long HealthyMinutes(long fromMs, long toMs)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM monitor_minute WHERE minute_ms >= $f AND minute_ms < $t AND ok = 1";
        cmd.Parameters.AddWithValue("$f", fromMs);
        cmd.Parameters.AddWithValue("$t", toMs);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public long DiscardedMinutes(long fromMs, long toMs)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM monitor_minute WHERE minute_ms >= $f AND minute_ms < $t AND ok = 0";
        cmd.Parameters.AddWithValue("$f", fromMs);
        cmd.Parameters.AddWithValue("$t", toMs);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void AddSamples(IReadOnlyList<ProbeSample> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO probe_sample (service, target, ts_ms, epoch, vantage, outcome, latency_ms)
            VALUES ($svc, $t, $ts, $e, $v, $o, $l)
            """;
        var p = new[] { "$svc", "$t", "$ts", "$e", "$v", "$o", "$l" }.Select(n => cmd.Parameters.Add(n, n is "$t" or "$v" ? SqliteType.Text : SqliteType.Integer)).ToArray();
        foreach (var s in samples)
        {
            object[] values = [(long)s.Service, s.Target.ToLowerInvariant(), s.TsMs, (long)s.Epoch, s.Vantage, (long)s.Outcome, s.LatencyMs];
            for (var i = 0; i < p.Length; i++)
            {
                p[i].Value = values[i];
            }

            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// One target's samples in [<paramref name="fromMs"/>, <paramref name="toMs"/>) from <paramref name="vantage"/>, only in
    /// healthy monitor minutes (a range on the sample PK, point lookups on the minute PK).
    /// </summary>
    public IReadOnlyList<ProbeSample> HealthySamples(byte service, string target, long fromMs, long toMs, string vantage)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.ts_ms, s.epoch, s.outcome, s.latency_ms FROM probe_sample s
            JOIN monitor_minute m ON m.minute_ms = (s.ts_ms / 60000) * 60000
            WHERE s.service = $svc AND s.target = $t AND s.ts_ms >= $f AND s.ts_ms < $to AND s.vantage = $v AND m.ok = 1
            """;
        cmd.Parameters.AddWithValue("$svc", (long)service);
        cmd.Parameters.AddWithValue("$t", target.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$f", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);
        cmd.Parameters.AddWithValue("$v", vantage);
        using var r = cmd.ExecuteReader();
        var samples = new List<ProbeSample>();
        while (r.Read())
        {
            samples.Add(new ProbeSample(service, target, r.GetInt64(0), (ulong)r.GetInt64(1), vantage, (ProbeOutcome)r.GetInt32(2), r.GetInt64(3)));
        }

        return samples;
    }

    /// <summary>Targets probed during <paramref name="epoch"/> (the report set), with each one's first sample time.</summary>
    public IReadOnlyList<(string Target, long FirstMs)> TargetsIn(byte service, ulong epoch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // idx_sample_epoch narrows to the epoch; the grouping is over one epoch's rows, once per epoch.
        cmd.CommandText = "SELECT target, MIN(ts_ms) FROM probe_sample WHERE epoch = $e AND service = $svc GROUP BY target";
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        cmd.Parameters.AddWithValue("$svc", (long)service);
        using var r = cmd.ExecuteReader();
        var targets = new List<(string, long)>();
        while (r.Read())
        {
            targets.Add((r.GetString(0), r.GetInt64(1)));
        }

        return targets;
    }

    /// <summary>Targets probed in <paramref name="epoch"/> that have no report row yet: one anti-join, not a lookup per target.</summary>
    public IReadOnlyList<(string Target, long FirstMs)> UnreportedTargets(string distributor, byte service, ulong epoch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.target, MIN(s.ts_ms) FROM probe_sample s
            WHERE s.epoch = $e AND s.service = $svc
              AND NOT EXISTS (SELECT 1 FROM verifier_report r WHERE r.distributor = $d AND r.service = $svc AND r.target = s.target AND r.epoch = $e)
            GROUP BY s.target
            """;
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        cmd.Parameters.AddWithValue("$svc", (long)service);
        cmd.Parameters.AddWithValue("$d", distributor.ToLowerInvariant());
        using var r = cmd.ExecuteReader();
        var targets = new List<(string, long)>();
        while (r.Read())
        {
            targets.Add((r.GetString(0), r.GetInt64(1)));
        }

        return targets;
    }

    public void RememberUrl(byte service, string target, ulong epoch, string url)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO verifier_target (service, target, epoch, url) VALUES ($svc, $t, $e, $u) ON CONFLICT DO UPDATE SET url = $u";
        cmd.Parameters.AddWithValue("$svc", (long)service);
        cmd.Parameters.AddWithValue("$t", target.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        cmd.Parameters.AddWithValue("$u", url);
        cmd.ExecuteNonQuery();
    }

    public string? UrlIn(byte service, string target, ulong epoch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT url FROM verifier_target WHERE service = $svc AND target = $t AND epoch = $e";
        cmd.Parameters.AddWithValue("$svc", (long)service);
        cmd.Parameters.AddWithValue("$t", target.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        return cmd.ExecuteScalar() as string;
    }

    public VerifierReport? GetReport(string distributor, byte service, string target, ulong epoch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ReportColumns} FROM verifier_report WHERE distributor = $d AND service = $svc AND target = $t AND epoch = $e";
        cmd.Parameters.AddWithValue("$d", distributor.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$svc", (long)service);
        cmd.Parameters.AddWithValue("$t", target.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>One epoch's reports still to sign or submit (the partial <c>idx_report_open_by_epoch</c>).</summary>
    public IReadOnlyList<VerifierReport> OpenReports(string distributor, ulong epoch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ReportColumns} FROM verifier_report WHERE distributor = $d AND epoch = $e AND state IN (0, 1, 2)";
        cmd.Parameters.AddWithValue("$d", distributor.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        using var r = cmd.ExecuteReader();
        var reports = new List<VerifierReport>();
        while (r.Read())
        {
            reports.Add(Read(r));
        }

        return reports;
    }

    /// <summary>The latest report per target, for the status card.</summary>
    public IReadOnlyList<VerifierReport> LatestReports(string distributor, ulong sinceEpoch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ReportColumns} FROM verifier_report WHERE distributor = $d AND epoch >= $e ORDER BY epoch DESC";
        cmd.Parameters.AddWithValue("$d", distributor.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$e", (long)sinceEpoch);
        using var r = cmd.ExecuteReader();
        var reports = new List<VerifierReport>();
        while (r.Read())
        {
            reports.Add(Read(r));
        }

        return [.. reports.GroupBy(x => (x.Service, x.Target)).Select(g => g.First())];
    }

    /// <summary>
    /// Inserts a report, or moves its state forward; measured values are never overwritten, and a state never moves back
    /// (states are ordered), so a second writer can't revert Submitted or reopen Terminal.
    /// </summary>
    public void SaveReport(VerifierReport r)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO verifier_report (distributor, service, target, epoch, values_csv, heartbeat_bps, bytes_claimed, flags, state, signer, signature, tx_hash, last_error, attempts, updated_ms)
            VALUES ($d, $svc, $t, $e, $vals, $hb, $bytes, $flags, $state, $signer, $sig, $tx, $err, $attempts, $updated)
            ON CONFLICT (distributor, service, target, epoch) DO UPDATE SET state = $state, signer = $signer, signature = $sig, tx_hash = $tx,
                last_error = $err, attempts = $attempts, updated_ms = $updated
            WHERE verifier_report.state <= $state AND verifier_report.state NOT IN (3, 4)
            """;
        object?[] values = [r.Distributor.ToLowerInvariant(), (long)r.Service, r.Target.ToLowerInvariant(), (long)r.Epoch, string.Join(',', r.Values),
            (long)r.HeartbeatBps, r.BytesClaimed, (long)r.Flags, (long)r.State, r.Signer, r.Signature, r.TxHash, r.LastError, r.Attempts, r.UpdatedMs];
        string[] names = ["$d", "$svc", "$t", "$e", "$vals", "$hb", "$bytes", "$flags", "$state", "$signer", "$sig", "$tx", "$err", "$attempts", "$updated"];
        for (var i = 0; i < names.Length; i++)
        {
            cmd.Parameters.AddWithValue(names[i], values[i] ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>Records <paramref name="epoch"/>'s assignments once (an empty list is recorded as a batch with no items).</summary>
    public void SaveAssignments(ulong epoch, IReadOnlyList<(string Target, byte Slot)> assignments)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO verifier_assignment (epoch, target, slot) VALUES ($e, $t, $s)";
        var (e, t, sl) = (cmd.Parameters.Add("$e", SqliteType.Integer), cmd.Parameters.Add("$t", SqliteType.Text), cmd.Parameters.Add("$s", SqliteType.Integer));
        foreach (var (target, slot) in assignments)
        {
            (e.Value, t.Value, sl.Value) = ((long)epoch, target.ToLowerInvariant(), (long)slot);
            cmd.ExecuteNonQuery();
        }

        cmd.Parameters.Clear();
        cmd.CommandText = "INSERT OR IGNORE INTO verifier_batch (epoch, state, items, updated_ms) VALUES ($e, $st, $n, 0)";
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        cmd.Parameters.AddWithValue("$st", (long)BatchState.Pending);
        cmd.Parameters.AddWithValue("$n", (long)assignments.Count);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public IReadOnlyList<(string Target, byte Slot)> AssignmentsIn(ulong epoch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT target, slot FROM verifier_assignment WHERE epoch = $e ORDER BY target";
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, byte)>();
        while (r.Read())
        {
            list.Add((r.GetString(0), (byte)r.GetInt32(1)));
        }

        return list;
    }

    public VerifierBatch? GetBatch(ulong epoch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT epoch, state, items, tx_hash, last_error, attempts FROM verifier_batch WHERE epoch = $e";
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new VerifierBatch((ulong)r.GetInt64(0), (BatchState)r.GetInt32(1), r.GetInt32(2), Text(r, 3), Text(r, 4), r.GetInt32(5)) : null;
    }

    /// <summary>Moves a batch forward (never back from Submitted or Terminal).</summary>
    public void SaveBatch(VerifierBatch b, long nowMs)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE verifier_batch SET state = $st, tx_hash = $tx, last_error = $err, attempts = $a, updated_ms = $u
            WHERE epoch = $e AND state NOT IN (2, 3)
            """;
        cmd.Parameters.AddWithValue("$e", (long)b.Epoch);
        cmd.Parameters.AddWithValue("$st", (long)b.State);
        cmd.Parameters.AddWithValue("$tx", (object?)b.TxHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)b.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", (long)b.Attempts);
        cmd.Parameters.AddWithValue("$u", nowMs);
        cmd.ExecuteNonQuery();
    }

    public long DirectorySeq(string node)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seq FROM directory_seen WHERE node = $n";
        cmd.Parameters.AddWithValue("$n", node.ToLowerInvariant());
        return cmd.ExecuteScalar() is long seq ? seq : 0;
    }

    public void SeeDirectory(string node, long seq)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO directory_seen (node, seq) VALUES ($n, $s) ON CONFLICT (node) DO UPDATE SET seq = MAX(seq, $s)";
        cmd.Parameters.AddWithValue("$n", node.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops samples, target URLs and minutes before <paramref name="epoch"/>, and finished reports more than
    /// <see cref="ReportRetentionEpochs"/> older (kept that long for the status card and #227).
    /// </summary>
    public int Sweep(ulong epoch, long beforeMs)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM probe_sample WHERE epoch < $e;
            DELETE FROM verifier_target WHERE epoch < $e;
            DELETE FROM monitor_minute WHERE minute_ms < $m;
            DELETE FROM verifier_report WHERE state IN (3, 4) AND epoch < $old;
            DELETE FROM verifier_assignment WHERE epoch < $e;
            DELETE FROM verifier_batch WHERE epoch < $old;
            """;
        cmd.Parameters.AddWithValue("$old", (long)epoch - ReportRetentionEpochs);
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        cmd.Parameters.AddWithValue("$m", beforeMs);
        return cmd.ExecuteNonQuery();
    }

    public const int ReportRetentionEpochs = 720;

    private const string ReportColumns =
        "distributor, service, target, epoch, values_csv, heartbeat_bps, bytes_claimed, flags, state, signer, signature, tx_hash, last_error, attempts, updated_ms";

    private static VerifierReport Read(SqliteDataReader r) => new(
        r.GetString(0), (byte)r.GetInt32(1), r.GetString(2), (ulong)r.GetInt64(3),
        [.. r.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse)], (uint)r.GetInt64(5),
        r.IsDBNull(6) ? null : r.GetInt64(6), (ReportFlags)r.GetInt32(7), (ReportState)r.GetInt32(8),
        Text(r, 9), Text(r, 10), Text(r, 11), Text(r, 12), r.GetInt32(13), r.GetInt64(14));

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }
}
