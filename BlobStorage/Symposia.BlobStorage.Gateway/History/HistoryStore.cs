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
            """;
        cmd.ExecuteNonQuery();
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
            INSERT INTO object_history (ts_ms, operation, bucket, key, api, size_bytes, etag, cid, client_ip, access_key_id, tenant_id, user_agent, request_id, outcome, status)
            VALUES ($ts, $op, $b, $k, $api, $size, $etag, $cid, $ip, $ak, $tenant, $ua, $rid, $outcome, $status)
            """;
        var integers = new HashSet<string> { "$ts", "$size", "$status" };
        var p = new[] { "$ts", "$op", "$b", "$k", "$api", "$size", "$etag", "$cid", "$ip", "$ak", "$tenant", "$ua", "$rid", "$outcome", "$status" }
            .Select(n => cmd.Parameters.Add(n, integers.Contains(n) ? SqliteType.Integer : SqliteType.Text)).ToArray();
        foreach (var e in entries)
        {
            object?[] values = [e.Timestamp.ToUnixTimeMilliseconds(), e.Operation, e.Bucket, e.Key, e.Api, e.Size, e.Etag, e.Cid, e.ClientIp,
                e.AccessKeyId, e.TenantId, e.UserAgent, e.RequestId, e.Outcome, e.Status];
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
