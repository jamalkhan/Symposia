using Microsoft.Data.Sqlite;

namespace Symposia.BlobStorage.Gateway.History;

/// <summary>
/// Operation history in its own SQLite file, <c>gateway-history.db</c>, in WAL mode (issue #204): SQLite
/// allows one writer per database, so keeping history out of the metadata DB means its writes never
/// queue behind object writes. Ids are AUTOINCREMENT, never reused, so keyset cursors are exact.
/// Indexes: (bucket, key, id) for per-object queries, (ts_ms) for the retention sweep.
/// </summary>
public sealed class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        DatabasePath = path;
        // DefaultTimeout doubles as SQLite's busy timeout, so a sweep and a batch wait for each other briefly.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA auto_vacuum = INCREMENTAL; -- before anything initialises the file, or it's ignored
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS object_history (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_ms         INTEGER NOT NULL,
                operation     TEXT NOT NULL CHECK (operation IN ('insert', 'update', 'view', 'delete')),
                bucket        TEXT NOT NULL,
                key           TEXT NOT NULL,
                api           TEXT NOT NULL CHECK (api IN ('s3', 'v1')),
                size_bytes    INTEGER,
                etag          TEXT,
                cid           TEXT,
                client_ip     TEXT,
                access_key_id TEXT,
                tenant_id     TEXT,
                user_agent    TEXT,
                request_id    TEXT NOT NULL,
                outcome       TEXT NOT NULL CHECK (outcome IN ('Success', 'NotFound', 'AccessDenied', 'Error')),
                status        INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_history_object ON object_history (bucket, key, id);
            CREATE INDEX IF NOT EXISTS idx_history_ts ON object_history (ts_ms);
            -- #221: one row per minute the gateway was up, for the uptime factor.
            CREATE TABLE IF NOT EXISTS gateway_liveness (minute_ms INTEGER PRIMARY KEY);
            -- #221/#225: each epoch's own factors, measured once and never changed. Keyed by the distributor
            -- too: a reset LocalDev chain starts over at epoch 0 under a new one. The gateway never signs
            -- these as a metric report; the Verifier reads the signed bytes-served figure (#225 FR3.4).
            CREATE TABLE IF NOT EXISTS epoch_measurement (
                distributor  TEXT NOT NULL,
                epoch        INTEGER NOT NULL,
                start_ms     INTEGER NOT NULL,
                end_ms       INTEGER NOT NULL,
                uptime_bps   INTEGER NOT NULL,
                success_bps  INTEGER NOT NULL,
                p95_ms       INTEGER NOT NULL,
                bytes_served INTEGER NOT NULL,
                requests     INTEGER NOT NULL,
                measured_ms  INTEGER NOT NULL,
                PRIMARY KEY (distributor, epoch)
            ) WITHOUT ROWID;
            """;
        cmd.ExecuteNonQuery();

        // #221: request duration, for the p95 latency factor. Rows written before this have none.
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('object_history') WHERE name = 'duration_ms'";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
        {
            cmd.CommandText = "ALTER TABLE object_history ADD COLUMN duration_ms INTEGER";
            cmd.ExecuteNonQuery();
        }

        // #225: #221's epoch_report carried signing state the gateway no longer has. Keep what it measured.
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'epoch_report'";
        if (Convert.ToInt64(cmd.ExecuteScalar()) > 0)
        {
            using var tx = conn.BeginTransaction();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO epoch_measurement (distributor, epoch, start_ms, end_ms, uptime_bps, success_bps, p95_ms, bytes_served, requests, measured_ms)
                SELECT distributor, epoch, start_ms, end_ms, uptime_bps, success_bps, p95_ms, bytes_served, requests, updated_ms
                FROM epoch_report WHERE NOT (state = 'Terminal' AND requests = 0 AND uptime_bps = 0);
                DROP TABLE epoch_report;
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
            cmd.Transaction = null;
        }
    }

    /// <summary>The database file, for disk accounting.</summary>
    public string DatabasePath { get; }

    /// <summary>Writes a batch in one transaction.</summary>
    public void Append(IReadOnlyList<HistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO object_history (ts_ms, operation, bucket, key, api, size_bytes, etag, cid, client_ip, access_key_id, tenant_id, user_agent, request_id, outcome, status, duration_ms)
            VALUES ($ts, $op, $b, $k, $api, $size, $etag, $cid, $ip, $ak, $tenant, $ua, $rid, $outcome, $status, $dur)
            """;
        var integers = new HashSet<string> { "$ts", "$size", "$status", "$dur" };
        var p = new[] { "$ts", "$op", "$b", "$k", "$api", "$size", "$etag", "$cid", "$ip", "$ak", "$tenant", "$ua", "$rid", "$outcome", "$status", "$dur" }
            .Select(n => cmd.Parameters.Add(n, integers.Contains(n) ? SqliteType.Integer : SqliteType.Text)).ToArray();
        foreach (var e in entries)
        {
            object?[] values = [e.Timestamp.ToUnixTimeMilliseconds(), e.Operation, e.Bucket, e.Key, e.Api, e.Size, e.Etag, e.Cid, e.ClientIp,
                e.AccessKeyId, e.TenantId, e.UserAgent, e.RequestId, e.Outcome, e.Status, e.DurationMs];
            for (var i = 0; i < p.Length; i++)
            {
                p[i].Value = values[i] ?? DBNull.Value;
            }

            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>One object's history, oldest first, after <paramref name="afterId"/>; one extra row signals another page.</summary>
    public IReadOnlyList<HistoryEntry> Query(string bucket, string key, DateTimeOffset? since, DateTimeOffset? until,
        IReadOnlyCollection<string>? operations, long afterId, int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder("SELECT id, ts_ms, operation, api, size_bytes, etag, cid, client_ip, access_key_id, tenant_id, user_agent, request_id, outcome, status " +
            "FROM object_history WHERE bucket = $b AND key = $k AND id > $after");
        cmd.Parameters.AddWithValue("$b", bucket);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$after", afterId);
        if (since is { } s)
        {
            sql.Append(" AND ts_ms >= $since");
            cmd.Parameters.AddWithValue("$since", s.ToUnixTimeMilliseconds());
        }

        if (until is { } u)
        {
            sql.Append(" AND ts_ms <= $until");
            cmd.Parameters.AddWithValue("$until", u.ToUnixTimeMilliseconds());
        }

        if (operations is { Count: > 0 })
        {
            var names = operations.Select((_, i) => "$op" + i).ToList();
            sql.Append($" AND operation IN ({string.Join(',', names)})");
            foreach (var (name, op) in names.Zip(operations))
            {
                cmd.Parameters.AddWithValue(name, op);
            }
        }

        sql.Append(" ORDER BY id LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = sql.ToString();

        using var r = cmd.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (r.Read())
        {
            entries.Add(new HistoryEntry(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2), bucket, key, r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), Text(r, 5), Text(r, 6), Text(r, 7), Text(r, 8), Text(r, 9), Text(r, 10),
                r.GetString(11), r.GetString(12), r.GetInt32(13)));
        }

        return entries;
    }

    /// <summary>Deletes up to <paramref name="batch"/> entries older than <paramref name="cutoff"/>; returns how many.</summary>
    public int DeleteOlderThan(DateTimeOffset cutoff, int batch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM object_history WHERE id IN (SELECT id FROM object_history WHERE ts_ms < $cutoff ORDER BY ts_ms LIMIT $n)";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$n", batch);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Marks the minute containing <paramref name="now"/> as up (#221); idempotent.</summary>
    public void RecordLiveness(DateTimeOffset now)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO gateway_liveness (minute_ms) VALUES ($m)";
        cmd.Parameters.AddWithValue("$m", now.ToUnixTimeMilliseconds() / 60_000 * 60_000);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The Blob Gateway factors over [<paramref name="startMs"/>, <paramref name="endMs"/>) (#221), in one read
    /// transaction. Success rate and p95 are per request (a multi-object request writes a row per object, all with
    /// its status and duration); bytes served is per object read. Every range scan uses <c>idx_history_ts</c>; the
    /// per-request grouping and the p95 sort are temp B-trees over the range, once per epoch, off the request path.
    /// </summary>
    public EpochFactors Aggregate(long startMs, long endMs)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(deferred: true);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$s", startMs);
        cmd.Parameters.AddWithValue("$e", endMs);

        cmd.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(failed), 0), COUNT(duration_ms)
            FROM (SELECT MAX(status >= 500) AS failed, MAX(duration_ms) AS duration_ms
                  FROM object_history WHERE ts_ms >= $s AND ts_ms < $e GROUP BY request_id)
            """;
        long requests, failures, timed;
        using (var r = cmd.ExecuteReader())
        {
            r.Read();
            (requests, failures, timed) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
        }

        cmd.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM object_history WHERE ts_ms >= $s AND ts_ms < $e AND operation = 'view' AND status < 400";
        var bytes = Convert.ToInt64(cmd.ExecuteScalar());

        long p95 = 0;
        if (timed > 0)
        {
            // Nearest rank: the ceil(0.95 n)-th smallest.
            cmd.CommandText = """
                SELECT d FROM (SELECT MAX(duration_ms) AS d FROM object_history WHERE ts_ms >= $s AND ts_ms < $e GROUP BY request_id)
                WHERE d IS NOT NULL ORDER BY d LIMIT 1 OFFSET $k
                """;
            cmd.Parameters.AddWithValue("$k", ((timed * 95) + 99) / 100 - 1);
            p95 = Convert.ToInt64(cmd.ExecuteScalar());
        }

        // Minutes overlapping the epoch, counted the same way on both sides: chain epochs needn't start on a minute.
        cmd.CommandText = "SELECT COUNT(*) FROM gateway_liveness WHERE minute_ms > $s - 60000 AND minute_ms < $e";
        var upMinutes = Convert.ToInt64(cmd.ExecuteScalar());
        var epochMinutes = Math.Max(1, ((endMs - 1) / 60_000) - (startMs / 60_000) + 1);

        tx.Commit();
        return new EpochFactors(
            UptimeBps: (uint)Math.Min(10_000, upMinutes * 10_000 / epochMinutes),
            SuccessBps: requests == 0 ? 10_000u : (uint)((requests - failures) * 10_000 / requests),
            P95Ms: p95,
            BytesServed: bytes,
            Requests: requests);
    }

    public EpochMeasurement? GetMeasurement(string distributor, ulong epoch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {MeasurementColumns} FROM epoch_measurement WHERE distributor = $d AND epoch = $e";
        cmd.Parameters.AddWithValue("$d", distributor.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$e", (long)epoch);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMeasurement(r) : null;
    }

    /// <summary>The most recent measurement, for diagnostics.</summary>
    public EpochMeasurement? LatestMeasurement()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {MeasurementColumns} FROM epoch_measurement ORDER BY measured_ms DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMeasurement(r) : null;
    }

    /// <summary>Stores a measurement once; a second save for the same epoch is ignored (measurements are immutable).</summary>
    public void SaveMeasurement(EpochMeasurement m)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO epoch_measurement (distributor, epoch, start_ms, end_ms, uptime_bps, success_bps, p95_ms, bytes_served, requests, measured_ms)
            VALUES ($dist, $epoch, $s, $e, $up, $ok, $p95, $bytes, $n, $at)
            """;
        object[] values = [m.Distributor.ToLowerInvariant(), (long)m.Epoch, m.StartMs, m.EndMs, (long)m.Factors.UptimeBps, (long)m.Factors.SuccessBps,
            m.Factors.P95Ms, m.Factors.BytesServed, m.Factors.Requests, m.MeasuredMs];
        string[] names = ["$dist", "$epoch", "$s", "$e", "$up", "$ok", "$p95", "$bytes", "$n", "$at"];
        for (var i = 0; i < names.Length; i++)
        {
            cmd.Parameters.AddWithValue(names[i], values[i]);
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes liveness minutes older than <paramref name="cutoff"/>.</summary>
    public int DeleteLivenessOlderThan(DateTimeOffset cutoff)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM gateway_liveness WHERE minute_ms < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }

    private const string MeasurementColumns =
        "distributor, epoch, start_ms, end_ms, uptime_bps, success_bps, p95_ms, bytes_served, requests, measured_ms";

    private static EpochMeasurement ReadMeasurement(SqliteDataReader r) => new(
        r.GetString(0), (ulong)r.GetInt64(1), r.GetInt64(2), r.GetInt64(3),
        new EpochFactors((uint)r.GetInt64(4), (uint)r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8)), r.GetInt64(9));

    /// <summary>The database's size on disk, including its WAL.</summary>
    public long SizeBytes => new[] { DatabasePath, DatabasePath + "-wal" }.Sum(f => File.Exists(f) ? new FileInfo(f).Length : 0);

    /// <summary>Folds the WAL back into the database and truncates it.</summary>
    public void Checkpoint()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns freed pages to the file system after a sweep.</summary>
    public void IncrementalVacuum()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA incremental_vacuum;";
        cmd.ExecuteNonQuery();
    }

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous = NORMAL;"; // per connection; WAL keeps it crash-safe, best-effort data needn't fsync per commit
        cmd.ExecuteNonQuery();
        return conn;
    }
}
