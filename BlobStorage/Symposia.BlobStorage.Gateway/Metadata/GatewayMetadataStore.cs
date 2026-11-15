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
    private readonly int _minCopies;

    public GatewayMetadataStore(IOptions<GatewayOptions> options)
    {
        _minCopies = options.Value.MinCopiesPerObject;
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
        // The older idx_objects_replica_count is left in place for one release so an N-1 binary restarted on
        // the same host against this database keeps its plan (issue #152); drop it once no N-1 hosts remain.
        // Two releases never write one metadata database concurrently: stop the old gateway before starting
        // the new one (from #16 on, an older binary doesn't know the tables soft-deleted data relies on).
        // Multipart uploads (#5): parts are ordinary blobs tracked here, never rows in objects, so they
        // can't leak into GET/LIST; the upload row outlives completion for idempotent Complete retries.
        Execute(conn, """
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
                completing_since     TEXT NULL,       -- lease: a Completing row older than the lease is released
                completed_at         TEXT NULL,
                final_cid            TEXT NULL,
                final_etag           TEXT NULL,
                committed_parts_json TEXT NULL
            );
            -- Only open uploads are listed; finished rows don't bloat the listing range (#5 review).
            CREATE INDEX IF NOT EXISTS idx_multipart_uploads_open ON multipart_uploads (bucket, key, upload_id) WHERE status = 'InProgress';
            CREATE INDEX IF NOT EXISTS idx_multipart_uploads_status_expiry ON multipart_uploads (status, expires_at);

            -- One pending deletion per (blob, node); collapse any duplicates from before the constraint.
            DELETE FROM gc_queue WHERE id NOT IN (SELECT MIN(id) FROM gc_queue GROUP BY cid, node_url);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_gc_queue_cid_node ON gc_queue (cid, node_url);

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

        InitializeTenancy(conn);
        InitializeReplication(conn, _minCopies);
        InitializeRedundancy(conn);
        InitializeGc(conn);

        // Schema generation, so a later release can refuse to run an older binary against a newer database
        // (review: Frank #3). 2 = soft delete, versions, shards (#7/#8/#16); 1 = everything before.
        Execute(conn, $"PRAGMA user_version = {SchemaGeneration}");
    }

    /// <summary>The metadata schema generation this build writes (PRAGMA user_version).</summary>
    public const int SchemaGeneration = 2;

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

    /// <summary>Every bucket with its creation time, by name (#203).</summary>
    public IReadOnlyList<(string Name, DateTimeOffset CreatedAt)> ListBuckets()
    {
        using var conn = Open();
        using var r = Query(conn, "SELECT name, created_at FROM buckets ORDER BY name").ExecuteReader();
        var buckets = new List<(string, DateTimeOffset)>();
        while (r.Read())
        {
            buckets.Add((r.GetString(0), DateTimeOffset.Parse(r.GetString(1))));
        }

        return buckets;
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
        using var cmd = Query(conn, $"SELECT {ObjectColumns} FROM objects WHERE bucket = $b AND key = $k", ("$b", bucket), ("$k", key));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadObject(r) : null;
    }

    /// <summary>The columns <see cref="ReadObject"/> reads, in order; every query returning whole objects selects these.</summary>
    internal const string ObjectColumns =
        "bucket, key, cid, size_bytes, content_type, last_modified, node_ids, etag_override, replication_state, target_copies, regions, " +
        "placement_gaps, merkle_root, scheme, merkle_cid";

    internal static ObjectRecord ReadObject(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1),
        Cid: r.GetString(2),
        SizeBytes: r.GetInt64(3),
        ContentType: r.GetString(4),
        LastModified: DateTimeOffset.Parse(r.GetString(5)),
        NodeIds: SplitNodeIds(r.GetString(6)),
        EtagOverride: r.IsDBNull(7) ? null : r.GetString(7))
    {
        Replication = r.GetString(8) == "quorum" ? ReplicationState.Quorum : ReplicationState.Full,
        TargetCopies = r.GetInt32(9),
        Regions = SplitList(r.IsDBNull(10) ? null : r.GetString(10)),
        PlacementGaps = ReadGaps(r.IsDBNull(11) ? null : r.GetString(11)),
        // A root counts only for the CID it was computed over: an N-1 gateway overwriting the row keeps the
        // old root, which must not be held against the new bytes (review: Frank #2).
        MerkleRoot = !r.IsDBNull(12) && !r.IsDBNull(14) && r.GetString(14) == r.GetString(2) ? r.GetString(12) : null,
        // Erasure-coded rows never list replicas; one that does was overwritten by an N-1 gateway as full copies.
        Scheme = r.GetString(13) != "replica" && !r.IsDBNull(6) && r.GetString(6).Length > 0 ? "replica" : r.GetString(13),
    };

    /// <summary>Inserts or replaces an object; true when it replaced one (read inside the write's transaction, #204).</summary>
    public bool PutObject(ObjectRecord record) => CommitObject(record).Replaced;

    /// <summary>
    /// Commits an object version under last-writer-wins (#7 FR11): the outcome says whether a row existed,
    /// whether this write became current, and the commit timestamp it was given.
    /// </summary>
    public CommitOutcome CommitObject(ObjectRecord record)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var outcome = UpsertObject(conn, tx, record);
        tx.Commit();
        return outcome;
    }

    /// <summary>
    /// The one write path for <c>objects</c> rows (PUT, COPY, multipart Complete). Inside the caller's
    /// transaction it also: cancels pending GC for the new CID (the blob is referenced again, so a
    /// deletion queued before this write must not run), and queues the overwritten blob for GC if
    /// nothing references it any more.
    /// </summary>
    private static CommitOutcome UpsertObject(SqliteConnection conn, SqliteTransaction tx, ObjectRecord record)
    {
        (string Cid, string[] NodeIds)? previous = null;
        bool existed;
        long previousSize = 0;
        var previousScheme = "replica";
        DateTimeOffset previousCommit = default;
        string previousEtag = "";
        using (var old = Query(conn, "SELECT cid, node_ids, size_bytes, last_modified, COALESCE(etag_override, cid), scheme FROM objects WHERE bucket = $b AND key = $k",
                   ("$b", record.Bucket), ("$k", record.Key)))
        {
            old.Transaction = tx;
            using var r = old.ExecuteReader();
            existed = r.Read();
            if (existed)
            {
                previousSize = r.GetInt64(2);
                previousCommit = DateTimeOffset.Parse(r.GetString(3));
                previousEtag = r.GetString(4);
                previousScheme = r.GetString(5);
            }

            if (existed && r.GetString(0) != record.Cid)
            {
                previous = (r.GetString(0), SplitNodeIds(r.GetString(1)));
            }
        }

        // Last writer wins by commit timestamp (#7 FR11). Commits are serialized by this IMMEDIATE
        // transaction, and the timestamp is kept monotonic per key (a slower clock can't move it back),
        // so a later commit normally wins outright. Two commits in the same millisecond are ordered by the
        // greater ETag instead, which every reader of this index sees the same way.
        var committedAt = TruncateToMilliseconds(record.LastModified);
        if (existed)
        {
            var floor = TruncateToMilliseconds(previousCommit);
            if (committedAt < floor)
            {
                // This commit is serialized after the current one, so it is the later write whatever its
                // gateway's clock says: it wins, one millisecond on (review: Frank #1). Only genuinely equal
                // timestamps fall to the ETag tiebreak.
                committedAt = floor.AddMilliseconds(1);
            }
            else if (committedAt == floor && string.CompareOrdinal(record.ETag, previousEtag) < 0)
            {
                // Lost the tiebreak: the existing version stays current, and this write's blob is garbage.
                CollectIfUnreferenced(conn, tx, record.Cid, record.NodeIds);
                return new CommitOutcome(existed, Applied: false, previousCommit);
            }
        }

        // Versioned bucket (#16 FR3): the overwritten version is kept as noncurrent, still billed, not collected.
        var archived = false;
        if (existed && previous is not null)
        {
            using var versioning = Query(conn, "SELECT versioning FROM bucket_lifecycle WHERE bucket = $b", ("$b", record.Bucket));
            versioning.Transaction = tx;
            if (versioning.ExecuteScalar() is long v && v != 0)
            {
                ArchiveVersion(conn, tx, record.Bucket, record.Key, "noncurrent", softDeleted: false);
                archived = true;
            }
        }

        if (existed && previousScheme != "replica" && previous is not null && !archived)
        {
            CollectShards(conn, tx, record.Bucket, record.Key, previous.Value.Cid); // the new version brings its own shards
        }

        var target = record.TargetCopies > 0 ? record.TargetCopies : (int?)null;
        Run(conn, tx, """
            INSERT INTO objects (bucket, key, cid, size_bytes, content_type, last_modified, node_ids, replica_count, etag_override,
                                 target_copies, replication_state, regions, placement_gaps, merkle_root, scheme, merkle_cid)
            VALUES ($b, $k, $cid, $sz, $ct, $lm, $ni, $rc, $eo,
                    COALESCE($tc, (SELECT target_copies FROM objects WHERE bucket = $b AND key = $k),
                             CAST((SELECT value FROM settings WHERE name = 'copy_floor') AS INTEGER), 1),
                    'full', $rg, $pg, $mr, $sc, $cid)
            ON CONFLICT(bucket, key) DO UPDATE SET
                cid = excluded.cid,
                size_bytes = excluded.size_bytes,
                content_type = excluded.content_type,
                last_modified = excluded.last_modified,
                node_ids = excluded.node_ids,
                replica_count = excluded.replica_count,
                etag_override = excluded.etag_override,
                target_copies = excluded.target_copies,
                regions = excluded.regions,
                placement_gaps = excluded.placement_gaps,
                merkle_root = excluded.merkle_root,
                scheme = excluded.scheme,
                merkle_cid = excluded.merkle_cid;
            """,
            ("$b", record.Bucket), ("$k", record.Key), ("$cid", record.Cid),
            ("$sz", record.SizeBytes), ("$ct", record.ContentType),
            ("$lm", committedAt.ToString("O")),
            ("$ni", string.Join(',', record.NodeIds)),
            ("$rc", record.NodeIds.Count),
            ("$eo", (object?)record.EtagOverride ?? DBNull.Value),
            ("$tc", target),
            ("$rg", record.Regions.Count == 0 ? null : string.Join(',', record.Regions)),
            ("$pg", GapsJson(record.PlacementGaps)), ("$mr", record.MerkleRoot), ("$sc", record.Scheme));
        RefreshReplicationState(conn, tx, "WHERE bucket = $b AND key = $k", ("$b", record.Bucket), ("$k", record.Key));
        CancelPendingDeletion(conn, tx, record.Cid);
        AdjustUsage(conn, tx, record.Bucket, archived ? record.SizeBytes : record.SizeBytes - previousSize);
        if (previous is { } p && !archived)
        {
            CollectIfUnreferenced(conn, tx, p.Cid, p.NodeIds);
        }

        return new CommitOutcome(existed, Applied: true, committedAt);
    }

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    /// <summary>
    /// Whether anything references a blob: an object (as its full-copy CID), a multipart part (#5), or a shard
    /// of an erasure-coded object (#8). Each arm is index-backed (idx_objects_cid, idx_multipart_parts_cid, idx_object_shards_cid).
    /// </summary>
    private const string ReferencedSql =
        "SELECT 1 FROM objects WHERE cid = $cid AND scheme = 'replica' UNION ALL SELECT 1 FROM multipart_parts WHERE cid = $cid " +
        "UNION ALL SELECT 1 FROM object_shards WHERE shard_cid = $cid " +
        // A soft-deleted or noncurrent version keeps its bytes until GC queues its purge (#16 FR2).
        "UNION ALL SELECT 1 FROM object_versions WHERE cid = $cid AND physically_deleted_at IS NULL AND purge_queued_at IS NULL LIMIT 1";

    /// <summary>A blob is referenced again: drop any queued deletion of it (GC/dedup race, #5 review).</summary>
    internal static void CancelPendingDeletion(SqliteConnection conn, SqliteTransaction tx, string cid)
    {
        Run(conn, tx, "DELETE FROM gc_queue WHERE cid = $cid", ("$cid", cid));
        // Versions whose purge waited on these bytes are done: the bytes now belong to a live object, which
        // is billed in its own right (review: Lenny #3, else they'd stay pending and billed forever).
        CloseVersionsWaitingOn(conn, tx, cid);
    }

    /// <summary>Queues a blob's copies for deletion if no object or multipart part references it (same transaction).</summary>
    internal static void CollectIfUnreferenced(SqliteConnection conn, SqliteTransaction tx, string cid, IEnumerable<string> nodeIds)
    {
        using (var check = Query(conn,
                   ReferencedSql, ("$cid", cid)))
        {
            check.Transaction = tx;
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }

        foreach (var node in nodeIds)
        {
            Run(conn, tx, "INSERT OR IGNORE INTO gc_queue (cid, node_url, queued_at) VALUES ($cid, $url, $t)",
                ("$cid", cid), ("$url", node), ("$t", Now()));
        }
    }

    /// <summary>Non-transactional convenience for callers outside the store (e.g. a blob written but never committed).</summary>
    public void CollectIfUnreferenced(string cid, IEnumerable<string> nodeIds)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        CollectIfUnreferenced(conn, tx, cid, nodeIds);
        tx.Commit();
    }

    private static void Run(SqliteConnection conn, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Query(conn, sql, args);
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes an object and, in the same IMMEDIATE transaction, queues its blob for GC if nothing
    /// else references the CID (#203 review). The physical delete is left to GcWorker after
    /// <c>GcMinAgeSeconds</c>, and an upload that dedups onto the CID meanwhile cancels it, so a
    /// concurrent identical PUT can't be left pointing at a deleted blob. Returns the removed record.
    /// </summary>
    public ObjectRecord? DeleteObjectAndCollect(string bucket, string key)
    {
        var record = GetObject(bucket, key);
        if (record is null)
        {
            return null;
        }

        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        using (var delete = Query(conn, "DELETE FROM objects WHERE bucket = $b AND key = $k AND cid = $cid",
                   ("$b", bucket), ("$k", key), ("$cid", record.Cid)))
        {
            delete.Transaction = tx;
            if (delete.ExecuteNonQuery() == 0)
            {
                return null; // replaced or deleted since the read; the newer write's record stands
            }
        }

        AdjustUsage(conn, tx, bucket, -record.SizeBytes);
        CollectIfUnreferenced(conn, tx, record.Cid, record.NodeIds);
        if (record.IsErasureCoded)
        {
            CollectShards(conn, tx, bucket, key, record.Cid);
        }

        tx.Commit();
        return record;
    }

    /// <summary>Returns false if the object did not exist (externally delete is always idempotent).</summary>
    public bool DeleteObject(string bucket, string key)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        long? size;
        using (var read = Query(conn, "SELECT size_bytes FROM objects WHERE bucket = $b AND key = $k", ("$b", bucket), ("$k", key)))
        {
            read.Transaction = tx;
            size = read.ExecuteScalar() as long?;
        }

        if (size is null)
        {
            return false;
        }

        Run(conn, tx, "DELETE FROM objects WHERE bucket = $b AND key = $k", ("$b", bucket), ("$k", key));
        AdjustUsage(conn, tx, bucket, -size.Value);
        tx.Commit();
        return true;
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
        return Query(conn, ReferencedSql, ("$cid", cid)).ExecuteScalar() is not null;
    }

    /// <summary>Returns the first object record that matches the given CID, or null if none exists.</summary>
    public ObjectRecord? GetObjectByCid(string cid)
    {
        using var conn = Open();
        using var cmd = Query(conn, $"SELECT {ObjectColumns} FROM objects WHERE cid = $cid LIMIT 1", ("$cid", cid));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadObject(r) : null;
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
            "UPDATE objects SET node_ids = $ni, replica_count = $rc, " + StateFromCount + " WHERE bucket = $b AND key = $k AND cid = $cid",
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
        // objects, in-flight multipart parts and the GC queue move together in one transaction (#5 review).
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        List<string> Rename(List<string> ids) =>
            ids.Select(u => u.Equals(oldUrl, StringComparison.OrdinalIgnoreCase) ? newUrl : u).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        const string contains = "WHERE instr(',' || node_ids || ',', ',' || $old || ',') > 0";

        var changed = ModifyNodeIds(conn, tx, contains, [("$old", oldUrl)], Rename);
        var parts = new List<(string Upload, int Part, List<string> Ids)>();
        using (var select = Query(conn, "SELECT upload_id, part_number, node_ids FROM multipart_parts " + contains, ("$old", oldUrl)))
        {
            select.Transaction = tx;
            using var r = select.ExecuteReader();
            while (r.Read())
            {
                parts.Add((r.GetString(0), r.GetInt32(1), SplitNodeIds(r.GetString(2)).ToList()));
            }
        }

        foreach (var (upload, part, ids) in parts)
        {
            Run(conn, tx, "UPDATE multipart_parts SET node_ids = $ni WHERE upload_id = $u AND part_number = $p",
                ("$ni", string.Join(',', Rename(ids))), ("$u", upload), ("$p", part));
        }

        // UNIQUE(cid, node_url): where the new name already has the entry, the old row is redundant.
        Run(conn, tx, "UPDATE OR IGNORE gc_queue SET node_url = $new WHERE node_url = $old", ("$new", newUrl), ("$old", oldUrl));
        Run(conn, tx, "DELETE FROM gc_queue WHERE node_url = $old", ("$old", oldUrl));
        tx.Commit();
        return changed + parts.Count;
    }

    private int ModifyNodeIds(string where, (string, object)[] parameters, Func<List<string>, List<string>> change)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var changed = ModifyNodeIds(conn, tx, where, parameters, change);
        tx.Commit();
        return changed;
    }

    private static int ModifyNodeIds(SqliteConnection conn, SqliteTransaction tx, string where, (string, object)[] parameters,
        Func<List<string>, List<string>> change)
    {
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

            using var update = Query(conn, "UPDATE objects SET node_ids = $ni, replica_count = $rc, " + StateFromCount + " WHERE bucket = $b AND key = $k",
                ("$ni", string.Join(',', next)), ("$rc", next.Count), ("$b", bucket), ("$k", key));
            update.Transaction = tx;
            changed += update.ExecuteNonQuery();
        }

        return changed;
    }

    /// <summary>Sets replication_state from the new replica count ($rc) in an UPDATE of node_ids (#7).</summary>
    private const string StateFromCount = "replication_state = CASE WHEN $rc >= target_copies THEN 'full' ELSE 'quorum' END";

    /// <summary>
    /// Pages through all objects in the metadata store.
    /// Used by the ReplicationMonitor and NodeReconciler.
    /// </summary>
    public IReadOnlyList<ObjectRecord> ListAllObjectsPaged((string Bucket, string Key)? after, int limit)
    {
        using var conn = Open();
        using var cmd = after is null
            ? Query(conn,
                $"SELECT {ObjectColumns} FROM objects ORDER BY bucket, key LIMIT $limit",
                ("$limit", limit))
            // Row-value comparison so the cursor seeks via idx_objects_bucket_key; the old
            // (bucket || '/' || key) expression forced a full scan per page (issue #135).
            : Query(conn,
                $"SELECT {ObjectColumns} FROM objects WHERE (bucket, key) > ($ab, $ak) ORDER BY bucket, key LIMIT $limit",
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
                $"SELECT {ObjectColumns} FROM objects WHERE replica_count BETWEEN 1 AND $max ORDER BY replica_count, bucket, key LIMIT $limit",
                ("$max", minCopies - 1), ("$limit", limit))
            : Query(conn,
                $"SELECT {ObjectColumns} FROM objects WHERE replica_count BETWEEN 1 AND $max AND (replica_count, bucket, key) > ($c, $b, $k) ORDER BY replica_count, bucket, key LIMIT $limit",
                ("$max", minCopies - 1), ("$c", after.Value.Count), ("$b", after.Value.Bucket), ("$k", after.Value.Key), ("$limit", limit));
        return ReadRecords(cmd);
    }

    /// <summary>How many objects are recorded on fewer than <paramref name="minCopies"/> nodes (idx_objects_replicas).</summary>
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
            results.Add(ReadObject(r));
        }
        return results;
    }

    // ── GC queue ──────────────────────────────────────────────────────────────

    public void EnqueueNodeDeletion(string cid, string nodeUrl, string category = "BlobDelete", int priority = PriorityMedium)
    {
        using var conn = Open();
        Execute(Query(conn,
            "INSERT OR IGNORE INTO gc_queue (cid, node_url, queued_at, category, priority) VALUES ($cid, $url, $t, $cat, $p)",
            ("$cid", cid), ("$url", nodeUrl), ("$t", Now()), ("$cat", category), ("$p", priority)));
    }

    public sealed record GcEntry(long Id, string Cid, string NodeUrl, int Attempts, string Category, int Priority);

    /// <summary>
    /// Due deletions by priority (#16 FR8.2: high, then medium, then low), oldest attempts first within a tier.
    /// <paramref name="reserveLow"/> entries of every batch are kept for the low tier, so a sustained high
    /// backlog defers low-priority work without starving it outright (FR8.3).
    /// </summary>
    public IReadOnlyList<GcEntry> DueDeletions(int limit, TimeSpan minAge, int reserveLow)
    {
        using var conn = Open();
        var cutoff = (DateTimeOffset.UtcNow - minAge).ToString("O");
        IReadOnlyList<GcEntry> Read(string where, int take)
        {
            using var r = Query(conn, $"SELECT id, cid, node_url, attempts, category, priority FROM gc_queue WHERE queued_at <= $cut AND {where} ORDER BY priority, attempts, id LIMIT $l",
                ("$cut", cutoff), ("$l", take)).ExecuteReader();
            var rows = new List<GcEntry>();
            while (r.Read()) rows.Add(new GcEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetInt32(5)));
            return rows;
        }

        var low = Read($"priority = {PriorityLow}", reserveLow);
        var rest = Read($"priority < {PriorityLow}", limit - low.Count);
        return rest.Count + low.Count < limit
            ? [.. rest, .. Read($"priority = {PriorityLow}", limit - rest.Count)]
            : [.. rest, .. low];
    }

    /// <summary>
    /// Deletions queued at least <paramref name="minAge"/> ago. The delay closes the window where a
    /// write of the same bytes (dedup) has reached the nodes but not yet committed its reference,
    /// which would cancel the entry (#5 review).
    /// </summary>
    public IReadOnlyList<(long Id, string Cid, string NodeUrl, int Attempts)> GetPendingDeletions(int limit, TimeSpan? minAge = null)
    {
        using var conn = Open();
        using var cmd = Query(conn,
            "SELECT id, cid, node_url, attempts FROM gc_queue WHERE queued_at <= $cutoff ORDER BY attempts, id LIMIT $limit",
            ("$cutoff", (DateTimeOffset.UtcNow - (minAge ?? TimeSpan.Zero)).ToString("O")), ("$limit", limit));
        using var r = cmd.ExecuteReader();
        var results = new List<(long, string, string, int)>();
        while (r.Read())
            results.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        return results;
    }

    /// <summary>Whether a GC entry is still queued (not cancelled by a re-reference since it was read).</summary>
    public bool IsQueued(long id)
    {
        using var conn = Open();
        return Query(conn, "SELECT 1 FROM gc_queue WHERE id = $id", ("$id", id)).ExecuteScalar() is not null;
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
            $"SELECT {ObjectColumns} FROM objects WHERE bucket = $b");

        cmd.Parameters.AddWithValue("$b", bucket);

        if (!string.IsNullOrEmpty(prefix))
        {
            // A key range rather than LIKE (#203): it stays on the (bucket, key) index, and it's
            // case-sensitive, where SQLite's LIKE folds ASCII case (prefix "A" matched "a..." keys).
            // The range bounds the index seek; substr filters to exact prefix matches inside it. The
            // upper bound (prefix + two U+10FFFF, the highest code point) misses only keys continuing
            // with two U+10FFFF noncharacters and more, which no client sends.
            sql.Append(" AND key >= $prefix AND key <= $prefixEnd AND substr(key, 1, length($prefix)) = $prefix");
            cmd.Parameters.AddWithValue("$prefix", prefix);
            cmd.Parameters.AddWithValue("$prefixEnd", prefix + char.ConvertFromUtf32(0x10FFFF) + char.ConvertFromUtf32(0x10FFFF));
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
            results.Add(ReadObject(r));
        }

        var truncated = results.Count > maxKeys;
        if (truncated) results.RemoveAt(results.Count - 1);

        return (results, truncated, truncated ? results[^1].Key : null);
    }

    private static string[] SplitNodeIds(string raw) =>
        raw.Length == 0 ? [] : raw.Split(',');

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

/// <summary>What committing an object version did (#7 FR11).</summary>
/// <param name="Replaced">A row already existed at the key.</param>
/// <param name="Applied">This version became current; false when it lost the same-millisecond tiebreak.</param>
/// <param name="CommittedAt">The version's commit timestamp (the current version's, when not applied).</param>
public sealed record CommitOutcome(bool Replaced, bool Applied, DateTimeOffset CommittedAt);
