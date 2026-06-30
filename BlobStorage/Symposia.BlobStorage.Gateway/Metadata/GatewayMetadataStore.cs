using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>
/// SQLite-backed derived metadata index for the gateway.
/// Not the source of truth (that is the node manifests + on-chain roots), but the fast-path
/// index for LIST, HEAD, and CopyObject operations.
/// See Requirements/BlobStorage/metadata-architecture.md#metadata-search-index.
/// </summary>
public sealed class GatewayMetadataStore
{
    private readonly string _connectionString;

    public GatewayMetadataStore(IOptions<GatewayOptions> options)
    {
        var dbPath = Path.GetFullPath(options.Value.MetadataDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public void Initialize()
    {
        using var conn = Open();
        Execute(conn, """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS buckets (
                name       TEXT PRIMARY KEY,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS objects (
                bucket        TEXT NOT NULL,
                key           TEXT NOT NULL,
                cid           TEXT NOT NULL,
                size_bytes    INTEGER NOT NULL,
                content_type  TEXT NOT NULL,
                last_modified TEXT NOT NULL,
                node_ids      TEXT NOT NULL,
                PRIMARY KEY (bucket, key)
            );

            CREATE INDEX IF NOT EXISTS idx_objects_bucket_key ON objects (bucket, key);
            """);
    }

    public bool BucketExists(string name)
    {
        using var conn = Open();
        return Query(conn, "SELECT 1 FROM buckets WHERE name = $n", ("$n", name)).ExecuteScalar() is not null;
    }

    public void CreateBucket(string name)
    {
        using var conn = Open();
        Execute(Query(conn,
            "INSERT OR IGNORE INTO buckets (name, created_at) VALUES ($n, $t)",
            ("$n", name), ("$t", Now())));
    }

    /// <summary>Returns false if the bucket is non-empty.</summary>
    public bool DeleteBucket(string name)
    {
        using var conn = Open();
        if (Query(conn, "SELECT 1 FROM objects WHERE bucket = $n LIMIT 1", ("$n", name)).ExecuteScalar() is not null)
            return false;
        Execute(Query(conn, "DELETE FROM buckets WHERE name = $n", ("$n", name)));
        return true;
    }

    public ObjectRecord? GetObject(string bucket, string key)
    {
        using var conn = Open();
        using var cmd = Query(conn,
            "SELECT cid, size_bytes, content_type, last_modified, node_ids FROM objects WHERE bucket = $b AND key = $k",
            ("$b", bucket), ("$k", key));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ObjectRecord(
            bucket, key,
            Cid: r.GetString(0),
            SizeBytes: r.GetInt64(1),
            ContentType: r.GetString(2),
            LastModified: DateTimeOffset.Parse(r.GetString(3)),
            NodeIds: SplitNodeIds(r.GetString(4)));
    }

    public void PutObject(ObjectRecord record)
    {
        using var conn = Open();
        Execute(Query(conn,
            """
            INSERT INTO objects (bucket, key, cid, size_bytes, content_type, last_modified, node_ids)
            VALUES ($b, $k, $cid, $sz, $ct, $lm, $ni)
            ON CONFLICT(bucket, key) DO UPDATE SET
                cid = excluded.cid,
                size_bytes = excluded.size_bytes,
                content_type = excluded.content_type,
                last_modified = excluded.last_modified,
                node_ids = excluded.node_ids;
            """,
            ("$b", record.Bucket), ("$k", record.Key), ("$cid", record.Cid),
            ("$sz", record.SizeBytes), ("$ct", record.ContentType),
            ("$lm", record.LastModified.ToString("O")),
            ("$ni", string.Join(',', record.NodeIds))));
    }

    /// <summary>Returns false if the object did not exist (externally delete is always idempotent).</summary>
    public bool DeleteObject(string bucket, string key)
    {
        using var conn = Open();
        return Execute(Query(conn,
            "DELETE FROM objects WHERE bucket = $b AND key = $k",
            ("$b", bucket), ("$k", key))) > 0;
    }

    public (IReadOnlyList<ObjectRecord> Objects, bool IsTruncated, string? NextContinuationToken) ListObjects(
        string bucket, string prefix, int maxKeys, string? continuationToken)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var sql = new System.Text.StringBuilder(
            "SELECT key, cid, size_bytes, content_type, last_modified, node_ids FROM objects WHERE bucket = $b");

        cmd.Parameters.AddWithValue("$b", bucket);

        if (!string.IsNullOrEmpty(prefix))
        {
            sql.Append(" AND key LIKE $prefix ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$prefix", EscapeLike(prefix) + "%");
        }

        if (!string.IsNullOrEmpty(continuationToken))
        {
            sql.Append(" AND key > $token");
            cmd.Parameters.AddWithValue("$token", continuationToken);
        }

        sql.Append(" ORDER BY key LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", maxKeys + 1); // one extra to detect truncation

        cmd.CommandText = sql.ToString();

        using var r = cmd.ExecuteReader();
        var results = new List<ObjectRecord>();
        while (r.Read())
        {
            results.Add(new ObjectRecord(
                bucket,
                Key: r.GetString(0),
                Cid: r.GetString(1),
                SizeBytes: r.GetInt64(2),
                ContentType: r.GetString(3),
                LastModified: DateTimeOffset.Parse(r.GetString(4)),
                NodeIds: SplitNodeIds(r.GetString(5))));
        }

        var truncated = results.Count > maxKeys;
        if (truncated) results.RemoveAt(results.Count - 1);

        return (results, truncated, truncated ? results[^1].Key : null);
    }

    private static string[] SplitNodeIds(string raw) =>
        raw.Length == 0 ? [] : raw.Split(',');

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    private static int Execute(SqliteCommand cmd) { using (cmd) return cmd.ExecuteNonQuery(); }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Query(SqliteConnection conn, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
