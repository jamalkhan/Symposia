using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>
/// SQLite-backed derived metadata index for the gateway.
/// Not the source of truth (that is the node manifests + on-chain roots), but the fast-path
/// index for LIST, HEAD, and CopyObject operations.
/// See Requirements/BlobStorage/metadata-architecture.md#metadata-search-index.
/// </summary>
public sealed partial class GatewayMetadataStore
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
            CREATE INDEX IF NOT EXISTS idx_objects_cid ON objects (cid);

            -- Pending node-side blob deletions: queued when DeleteObject is called but a node is
            -- unreachable, or when a replica is confirmed corrupt and must be purged.
            CREATE TABLE IF NOT EXISTS gc_queue (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                cid           TEXT NOT NULL,
                node_url      TEXT NOT NULL,
                queued_at     TEXT NOT NULL,
                attempts      INTEGER NOT NULL DEFAULT 0,
                last_attempt  TEXT NULL
            );

            -- Dynamically registered nodes (added via admin API or auto-discovered).
            -- Config-file nodes are not persisted here; they are merged at startup.
            CREATE TABLE IF NOT EXISTS nodes (
                url        TEXT PRIMARY KEY,
                added_at   TEXT NOT NULL,
                source     TEXT NOT NULL   -- 'config' | 'admin'
            );
            """);

        // replica_count mirrors the number of entries in node_ids so "which objects have too few
        // copies" is an index lookup instead of a full scan (issue #135). Added in place for
        // existing databases, then backfilled once from node_ids (comma separated).
        // Check, ALTER and backfill run in one IMMEDIATE transaction: two processes starting together
        // can't both add the column, and a crash can't leave the column added but never backfilled.
        using (var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false))
        {
            bool hasReplicaCount;
            using (var pragma = Query(conn, "SELECT 1 FROM pragma_table_info('objects') WHERE name = 'replica_count'"))
            {
                pragma.Transaction = tx;
                hasReplicaCount = pragma.ExecuteScalar() is not null;
            }

            // etag_override (#5): the S3 multipart ETag for objects assembled from parts; NULL otherwise.
            bool hasEtagOverride;
            using (var pragma = Query(conn, "SELECT 1 FROM pragma_table_info('objects') WHERE name = 'etag_override'"))
            {
                pragma.Transaction = tx;
                hasEtagOverride = pragma.ExecuteScalar() is not null;
            }

            if (!hasEtagOverride)
            {
                using var add = Query(conn, "ALTER TABLE objects ADD COLUMN etag_override TEXT NULL;");
                add.Transaction = tx;
                add.ExecuteNonQuery();
            }

            if (!hasReplicaCount)
            {
                using var migrate = Query(conn, """
                    ALTER TABLE objects ADD COLUMN replica_count INTEGER NOT NULL DEFAULT 0;
                    UPDATE objects SET replica_count =
                        CASE WHEN node_ids = '' THEN 0
                             ELSE length(node_ids) - length(replace(node_ids, ',', '')) + 1 END;
                    """);
                migrate.Transaction = tx;
                migrate.ExecuteNonQuery();
            }

            tx.Commit();
        }

        // (replica_count, bucket, key) lets the under-replication sweep range-scan and page in key order.
        // Multipart uploads (#5): parts are ordinary blobs tracked here, never rows in objects, so they
        // can't leak into GET/LIST; the upload row outlives completion for idempotent Complete retries.
        Execute(conn, """
            DROP INDEX IF EXISTS idx_objects_replica_count;
            CREATE INDEX IF NOT EXISTS idx_objects_replicas ON objects (replica_count, bucket, key);

            CREATE TABLE IF NOT EXISTS multipart_uploads (
                upload_id            TEXT PRIMARY KEY,
                tenant_id            TEXT NOT NULL,
                bucket               TEXT NOT NULL,
                key                  TEXT NOT NULL,
                content_type         TEXT NOT NULL,
                checksum_algorithm   TEXT NULL,
                checksum_type        TEXT NULL,
                initiated_at         TEXT NOT NULL,
                expires_at           TEXT NOT NULL,
                status               TEXT NOT NULL,   -- InProgress | Completing | Completed | Aborted
                completed_at         TEXT NULL,
                final_cid            TEXT NULL,
                final_etag           TEXT NULL,
                committed_parts_json TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_multipart_uploads_bucket_key ON multipart_uploads (bucket, key, upload_id);

            CREATE TABLE IF NOT EXISTS multipart_parts (
                upload_id    TEXT NOT NULL,
                part_number  INTEGER NOT NULL,
                cid          TEXT NOT NULL,
                size_bytes   INTEGER NOT NULL,
                node_ids     TEXT NOT NULL,
                checksum     TEXT NULL,
                uploaded_at  TEXT NOT NULL,
                PRIMARY KEY (upload_id, part_number)
            );
            CREATE INDEX IF NOT EXISTS idx_multipart_parts_cid ON multipart_parts (cid);
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
            "SELECT cid, size_bytes, content_type, last_modified, node_ids, etag_override FROM objects WHERE bucket = $b AND key = $k",
            ("$b", bucket), ("$k", key));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ObjectRecord(
            bucket, key,
            Cid: r.GetString(0),
            SizeBytes: r.GetInt64(1),
            ContentType: r.GetString(2),
            LastModified: DateTimeOffset.Parse(r.GetString(3)),
            NodeIds: SplitNodeIds(r.GetString(4)),
            EtagOverride: r.IsDBNull(5) ? null : r.GetString(5));
    }

    public void PutObject(ObjectRecord record)
    {
        using var conn = Open();
        Execute(Query(conn,
            """
            INSERT INTO objects (bucket, key, cid, size_bytes, content_type, last_modified, node_ids, replica_count, etag_override)
            VALUES ($b, $k, $cid, $sz, $ct, $lm, $ni, $rc, $eo)
            ON CONFLICT(bucket, key) DO UPDATE SET
                cid = excluded.cid,
                size_bytes = excluded.size_bytes,
                content_type = excluded.content_type,
                last_modified = excluded.last_modified,
                node_ids = excluded.node_ids,
                replica_count = excluded.replica_count,
                etag_override = excluded.etag_override;
            """,
            ("$b", record.Bucket), ("$k", record.Key), ("$cid", record.Cid),
            ("$sz", record.SizeBytes), ("$ct", record.ContentType),
            ("$lm", record.LastModified.ToString("O")),
            ("$ni", string.Join(',', record.NodeIds)),
            ("$rc", record.NodeIds.Count),
            ("$eo", (object?)record.EtagOverride ?? DBNull.Value)));
    }

    /// <summary>Returns false if the object did not exist (externally delete is always idempotent).</summary>
    public bool DeleteObject(string bucket, string key)
    {
        using var conn = Open();
        return Execute(Query(conn,
            "DELETE FROM objects WHERE bucket = $b AND key = $k",
            ("$b", bucket), ("$k", key))) > 0;
    }

    // ── CID reference counting ────────────────────────────────────────────────

    /// <summary>
    /// Returns true if any object still references this CID.
    /// Used by GcWorker to skip blob deletion when multiple keys share the same content (dedup).
    /// </summary>
    public bool IsCidReferenced(string cid)
    {
        using var conn = Open();
        // A part of an in-progress multipart upload is referenced too (#5), so GC must not delete it.
        return Query(conn,
            "SELECT 1 FROM objects WHERE cid = $cid UNION ALL SELECT 1 FROM multipart_parts WHERE cid = $cid LIMIT 1",
            ("$cid", cid)).ExecuteScalar() is not null;
    }

    /// <summary>Returns the first object record that matches the given CID, or null if none exists.</summary>
    public ObjectRecord? GetObjectByCid(string cid)
    {
        using var conn = Open();
        using var cmd = Query(conn,
            "SELECT bucket, key, size_bytes, content_type, last_modified, node_ids FROM objects WHERE cid = $cid LIMIT 1",
            ("$cid", cid));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ObjectRecord(
            r.GetString(0), r.GetString(1), cid,
            r.GetInt64(2), r.GetString(3),
            DateTimeOffset.Parse(r.GetString(4)),
            SplitNodeIds(r.GetString(5)));
    }

    /// <summary>
    /// Updates the node_ids list for an existing object record.
    /// Used by RepairWorker after successfully re-replicating a blob to a new node.
    /// </summary>
    /// <returns>False if the key no longer holds <paramref name="cid"/> (overwritten or deleted meanwhile):
    /// a replica list read for one version must never be written onto another (issue #135 review).</returns>
    public bool UpdateNodeIds(string bucket, string key, string cid, IReadOnlyList<string> nodeIds)
    {
        using var conn = Open();
        return Execute(Query(conn,
            "UPDATE objects SET node_ids = $ni, replica_count = $rc WHERE bucket = $b AND key = $k AND cid = $cid",
            ("$ni", string.Join(',', nodeIds)), ("$rc", nodeIds.Count), ("$b", bucket), ("$k", key), ("$cid", cid))) > 0;
    }

    /// <summary>
    /// Atomically removes <paramref name="nodeUrl"/> from the object's replicas, re-reading the
    /// current list inside the transaction, and only if the key still holds <paramref name="cid"/>.
    /// </summary>
    public bool RemoveReplica(string bucket, string key, string cid, string nodeUrl) =>
        ModifyNodeIds("WHERE bucket = $b AND key = $k AND cid = $cid", [("$b", bucket), ("$k", key), ("$cid", cid)],
            ids => ids.Where(u => !u.Equals(nodeUrl, StringComparison.OrdinalIgnoreCase)).ToList()) > 0;

    /// <summary>Adds <paramref name="nodeUrl"/> to every object holding <paramref name="cid"/> (idx_objects_cid); returns rows changed.</summary>
    public int AddReplicaForCid(string cid, string nodeUrl) =>
        ModifyNodeIds("WHERE cid = $cid", [("$cid", cid)],
            ids => ids.Contains(nodeUrl, StringComparer.OrdinalIgnoreCase) ? ids : [.. ids, nodeUrl]);

    /// <summary>
    /// Re-points replicas and pending deletions from a node's old URL to its new one when the node
    /// re-registers at a different address, so its copies stay counted and readable. Full scan of
    /// objects, but only on the rare address-change event.
    /// </summary>
    public int RenameReplicaUrl(string oldUrl, string newUrl)
    {
        var changed = ModifyNodeIds("WHERE instr(',' || node_ids || ',', ',' || $old || ',') > 0", [("$old", oldUrl)],
            ids => ids.Select(u => u.Equals(oldUrl, StringComparison.OrdinalIgnoreCase) ? newUrl : u)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        using var conn = Open();
        Execute(Query(conn, "UPDATE gc_queue SET node_url = $new WHERE node_url = $old", ("$new", newUrl), ("$old", oldUrl)));
        return changed;
    }

    private int ModifyNodeIds(string where, (string, object)[] parameters, Func<List<string>, List<string>> change)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var rows = new List<(string Bucket, string Key, List<string> Ids)>();
        using (var select = Query(conn, "SELECT bucket, key, node_ids FROM objects " + where, parameters))
        {
            select.Transaction = tx;
            using var r = select.ExecuteReader();
            while (r.Read())
            {
                rows.Add((r.GetString(0), r.GetString(1), SplitNodeIds(r.GetString(2)).ToList()));
            }
        }

        var changed = 0;
        foreach (var (bucket, key, ids) in rows)
        {
            var next = change(ids);
            if (next.SequenceEqual(ids, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            using var update = Query(conn, "UPDATE objects SET node_ids = $ni, replica_count = $rc WHERE bucket = $b AND key = $k",
                ("$ni", string.Join(',', next)), ("$rc", next.Count), ("$b", bucket), ("$k", key));
            update.Transaction = tx;
            changed += update.ExecuteNonQuery();
        }

        tx.Commit();
        return changed;
    }

    /// <summary>
    /// Pages through all objects in the metadata store.
    /// Used by the ReplicationMonitor and NodeReconciler.
    /// </summary>
    public IReadOnlyList<ObjectRecord> ListAllObjectsPaged((string Bucket, string Key)? after, int limit)
    {
        using var conn = Open();
        using var cmd = after is null
            ? Query(conn,
                "SELECT bucket, key, cid, size_bytes, content_type, last_modified, node_ids FROM objects ORDER BY bucket, key LIMIT $limit",
                ("$limit", limit))
            // Row-value comparison so the cursor seeks via idx_objects_bucket_key; the old
            // (bucket || '/' || key) expression forced a full scan per page (issue #135).
            : Query(conn,
                "SELECT bucket, key, cid, size_bytes, content_type, last_modified, node_ids FROM objects WHERE (bucket, key) > ($ab, $ak) ORDER BY bucket, key LIMIT $limit",
                ("$ab", after.Value.Bucket), ("$ak", after.Value.Key), ("$limit", limit));

        return ReadRecords(cmd);
    }

    /// <summary>
    /// Up to <paramref name="limit"/> repairable objects (at least one copy, fewer than
    /// <paramref name="minCopies"/>) after the cursor, fewest copies first. Objects with no copies
    /// can't be repaired and are excluded so they can't starve the rest (see CountUnderReplicated).
    /// Backed by idx_objects_replicas (replica_count, bucket, key).
    /// </summary>
    public IReadOnlyList<ObjectRecord> ListUnderReplicated(int minCopies, int limit, (int Count, string Bucket, string Key)? after = null)
    {
        using var conn = Open();
        using var cmd = after is null
            ? Query(conn,
                "SELECT bucket, key, cid, size_bytes, content_type, last_modified, node_ids FROM objects WHERE replica_count BETWEEN 1 AND $max ORDER BY replica_count, bucket, key LIMIT $limit",
                ("$max", minCopies - 1), ("$limit", limit))
            : Query(conn,
                "SELECT bucket, key, cid, size_bytes, content_type, last_modified, node_ids FROM objects WHERE replica_count BETWEEN 1 AND $max AND (replica_count, bucket, key) > ($c, $b, $k) ORDER BY replica_count, bucket, key LIMIT $limit",
                ("$max", minCopies - 1), ("$c", after.Value.Count), ("$b", after.Value.Bucket), ("$k", after.Value.Key), ("$limit", limit));
        return ReadRecords(cmd);
    }

    /// <summary>How many objects are recorded on fewer than <paramref name="minCopies"/> nodes (idx_objects_replica_count).</summary>
    public long CountUnderReplicated(int minCopies)
    {
        using var conn = Open();
        using var cmd = Query(conn, "SELECT COUNT(*) FROM objects WHERE replica_count < $min", ("$min", minCopies));
        return (long)cmd.ExecuteScalar()!;
    }

    private static IReadOnlyList<ObjectRecord> ReadRecords(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var results = new List<ObjectRecord>();
        while (r.Read())
        {
            results.Add(new ObjectRecord(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetInt64(3), r.GetString(4),
                DateTimeOffset.Parse(r.GetString(5)),
                SplitNodeIds(r.GetString(6))));
        }
        return results;
    }

    // ── GC queue ──────────────────────────────────────────────────────────────

    public void EnqueueNodeDeletion(string cid, string nodeUrl)
    {
        using var conn = Open();
        Execute(Query(conn,
            "INSERT OR IGNORE INTO gc_queue (cid, node_url, queued_at) VALUES ($cid, $url, $t)",
            ("$cid", cid), ("$url", nodeUrl), ("$t", Now())));
    }

    public IReadOnlyList<(long Id, string Cid, string NodeUrl, int Attempts)> GetPendingDeletions(int limit)
    {
        using var conn = Open();
        using var cmd = Query(conn,
            "SELECT id, cid, node_url, attempts FROM gc_queue ORDER BY attempts, id LIMIT $limit",
            ("$limit", limit));
        using var r = cmd.ExecuteReader();
        var results = new List<(long, string, string, int)>();
        while (r.Read())
            results.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        return results;
    }

    public void RemoveFromGcQueue(long id)
    {
        using var conn = Open();
        Execute(Query(conn, "DELETE FROM gc_queue WHERE id = $id", ("$id", id)));
    }

    public void IncrementGcAttempt(long id)
    {
        using var conn = Open();
        Execute(Query(conn,
            "UPDATE gc_queue SET attempts = attempts + 1, last_attempt = $t WHERE id = $id",
            ("$t", Now()), ("$id", id)));
    }

    // ── Dynamic node registry ─────────────────────────────────────────────────

    public IReadOnlyList<string> GetPersistedNodes()
    {
        using var conn = Open();
        using var cmd = Query(conn, "SELECT url FROM nodes ORDER BY added_at");
        using var r = cmd.ExecuteReader();
        var results = new List<string>();
        while (r.Read()) results.Add(r.GetString(0));
        return results;
    }

    public void PersistNode(string url, string source)
    {
        using var conn = Open();
        Execute(Query(conn,
            "INSERT OR IGNORE INTO nodes (url, added_at, source) VALUES ($url, $t, $src)",
            ("$url", url), ("$t", Now()), ("$src", source)));
    }

    public void RemovePersistedNode(string url)
    {
        using var conn = Open();
        Execute(Query(conn, "DELETE FROM nodes WHERE url = $url", ("$url", url)));
    }

    public (IReadOnlyList<ObjectRecord> Objects, bool IsTruncated, string? NextContinuationToken) ListObjects(
        string bucket, string prefix, int maxKeys, string? continuationToken)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var sql = new System.Text.StringBuilder(
            "SELECT key, cid, size_bytes, content_type, last_modified, node_ids, etag_override FROM objects WHERE bucket = $b");

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
                NodeIds: SplitNodeIds(r.GetString(5)),
                EtagOverride: r.IsDBNull(6) ? null : r.GetString(6)));
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
