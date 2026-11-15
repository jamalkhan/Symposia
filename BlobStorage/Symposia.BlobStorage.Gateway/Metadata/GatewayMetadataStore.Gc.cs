using Microsoft.Data.Sqlite;

namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>A non-current object version: soft-deleted, superseded under versioning, or a delete marker (#16).</summary>
public sealed record ObjectVersion(
    long Id, string Bucket, string Key, string Cid, long SizeBytes, string Kind, IReadOnlyList<string> NodeIds,
    DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt, DateTimeOffset? LockedUntil, bool LegalHold, DateTimeOffset? PhysicallyDeletedAt);

/// <summary>A bucket's lifecycle and retention rules (#16 FR1–FR3). Null fields take the gateway defaults.</summary>
public sealed record BucketLifecycle(
    bool Versioning = false, int? MaxVersions = null, int? MaxVersionAgeDays = null, int? MinRetentionDays = null,
    int? RecoveryDays = null, int? MultipartTtlHours = null);

/// <summary>
/// Garbage collection state (#16). The <c>objects</c> table keeps only what the API can read; everything GC
/// still has to decide about lives beside it: <c>object_versions</c> (soft-deleted and noncurrent versions
/// and delete markers, kept as audit records after their bytes are purged), bucket lifecycle rules,
/// <c>blob_events</c> (billing and notifications), orphan candidates (48 h grace), offline replica marks and
/// placement warnings. The existing <c>gc_queue</c> is the one physical-deletion queue, now prioritised.
/// </summary>
public sealed partial class GatewayMetadataStore
{
    public const int PriorityHigh = 0, PriorityMedium = 1, PriorityLow = 2;

    private static void InitializeGc(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        // gc_queue becomes the unified task queue (additive migration: old rows are medium-priority blob deletes).
        AddColumnIfMissing(conn, tx, "gc_queue", "category", "TEXT NOT NULL DEFAULT 'BlobDelete'");
        AddColumnIfMissing(conn, tx, "gc_queue", "priority", $"INTEGER NOT NULL DEFAULT {PriorityMedium}");
        AddColumnIfMissing(conn, tx, "objects", "locked_until", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "objects", "legal_hold", "INTEGER NOT NULL DEFAULT 0");
        tx.Commit();
        using var tx2 = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        InitializeGcTables(conn, tx2);
        // Everything a restore needs to bring a version back exactly as it was (review: Lenny #4, Frank #12).
        foreach (var (column, definition) in new[]
                 {
                     ("content_type", "TEXT NULL"), ("etag_override", "TEXT NULL"), ("regions", "TEXT NULL"), ("placement_gaps", "TEXT NULL"),
                     ("merkle_root", "TEXT NULL"), ("merkle_cid", "TEXT NULL"), ("scheme", "TEXT NOT NULL DEFAULT 'replica'"), ("target_copies", "INTEGER NULL"),
                 })
        {
            AddColumnIfMissing(conn, tx2, "object_versions", column, definition);
        }

        tx2.Commit();
    }

    private static void InitializeGcTables(SqliteConnection conn, SqliteTransaction tx)
    {
        Run(conn, tx, """
            CREATE INDEX IF NOT EXISTS idx_gc_queue_due ON gc_queue (priority, attempts, id, queued_at); -- matches DueDeletions' ORDER BY, so LIMIT stops early

            CREATE TABLE IF NOT EXISTS object_versions (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                bucket                TEXT NOT NULL,
                key                   TEXT NOT NULL,
                cid                   TEXT NOT NULL,
                size_bytes            INTEGER NOT NULL,
                kind                  TEXT NOT NULL,   -- deleted | noncurrent | marker
                node_ids              TEXT NOT NULL,
                created_at            TEXT NOT NULL,
                deleted_at            TEXT NULL,       -- when it became soft-deleted (recovery window starts)
                locked_until          TEXT NULL,
                legal_hold            INTEGER NOT NULL DEFAULT 0,
                purge_queued_at       TEXT NULL,
                physically_deleted_at TEXT NULL        -- every replica confirmed gone; the row stays as the audit record
            );
            CREATE INDEX IF NOT EXISTS idx_versions_key ON object_versions (bucket, key, created_at);
            CREATE INDEX IF NOT EXISTS idx_versions_markers ON object_versions (bucket, key) WHERE kind = 'marker';
            CREATE INDEX IF NOT EXISTS idx_versions_pending ON object_versions (bucket) WHERE physically_deleted_at IS NULL AND deleted_at IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_versions_purgeable ON object_versions (deleted_at) WHERE purge_queued_at IS NULL AND deleted_at IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_versions_cid_live ON object_versions (cid) WHERE physically_deleted_at IS NULL;
            CREATE INDEX IF NOT EXISTS idx_versions_queued ON object_versions (cid) WHERE purge_queued_at IS NOT NULL AND physically_deleted_at IS NULL;

            -- Which blobs each version's purge waits on: its CID, or its shards' CIDs (#8 erasure coding).
            CREATE TABLE IF NOT EXISTS version_purges (
                version_id INTEGER NOT NULL,
                cid        TEXT NOT NULL,
                PRIMARY KEY (version_id, cid)
            );
            CREATE INDEX IF NOT EXISTS idx_version_purges_cid ON version_purges (cid);

            CREATE TABLE IF NOT EXISTS bucket_lifecycle (
                bucket            TEXT PRIMARY KEY,
                versioning        INTEGER NOT NULL DEFAULT 0,
                max_versions      INTEGER NULL,
                max_version_age_d INTEGER NULL,
                min_retention_d   INTEGER NULL,
                recovery_d        INTEGER NULL,
                multipart_ttl_h   INTEGER NULL
            );

            -- blob.multipart_aborted / blob.physically_deleted (#16 FR1.2, FR9): what billing and notifications consume.
            CREATE TABLE IF NOT EXISTS blob_events (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                type       TEXT NOT NULL,
                bucket     TEXT NULL,
                key        TEXT NULL,
                cid        TEXT NULL,
                size_bytes INTEGER NULL,
                at         TEXT NOT NULL
            );

            -- Blobs a node holds that nothing references, from first sighting (#16 FR5.2: 48 h grace from here).
            CREATE TABLE IF NOT EXISTS orphan_candidates (
                cid           TEXT NOT NULL,
                node_id       TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                PRIMARY KEY (cid, node_id)
            );

            -- Replica references on a node past its grace period (#16 FR4.1), until re-replication confirms.
            CREATE TABLE IF NOT EXISTS offline_replicas (
                bucket  TEXT NOT NULL,
                key     TEXT NOT NULL,
                cid     TEXT NOT NULL,
                node_id TEXT NOT NULL,
                since   TEXT NOT NULL,
                PRIMARY KEY (bucket, key, node_id)
            );

            -- Offline references that can't be re-replicated (#16 FR4.4): shown, never silently dropped.
            CREATE TABLE IF NOT EXISTS placement_warnings (
                cid            TEXT NOT NULL,
                reason         TEXT NOT NULL,
                first_flagged  TEXT NOT NULL,
                cleared_at     TEXT NULL,
                PRIMARY KEY (cid)
            );
            """);
    }

    // ── Versions and soft delete (#16 FR2, FR3) ───────────────────────────────

    /// <summary>Moves a live object into object_versions inside the caller's transaction.</summary>
    internal static void ArchiveVersion(SqliteConnection conn, SqliteTransaction tx, string bucket, string key, string kind, bool softDeleted)
    {
        Run(conn, tx, $"""
            INSERT INTO object_versions (bucket, key, cid, size_bytes, kind, node_ids, created_at, deleted_at, locked_until, legal_hold,
                                         content_type, etag_override, regions, placement_gaps, merkle_root, merkle_cid, scheme, target_copies)
            SELECT bucket, key, cid, size_bytes, $kind, node_ids, last_modified, {(softDeleted ? "$now" : "NULL")}, locked_until, legal_hold,
                   content_type, etag_override, regions, placement_gaps, merkle_root, merkle_cid, scheme, target_copies
            FROM objects WHERE bucket = $b AND key = $k
            """, ("$kind", kind), ("$now", Now()), ("$b", bucket), ("$k", key));
    }

    /// <summary>
    /// Deletes an object through the soft-delete path (#16 FR2): the API stops seeing it at once, its bytes stay
    /// for the recovery window, and it counts toward storage (and billing) until physically deleted (FR9).
    /// Versioned buckets also leave a delete marker. Returns the removed record.
    /// </summary>
    public ObjectRecord? SoftDeleteObject(string bucket, string key)
    {
        var record = GetObject(bucket, key);
        if (record is null)
        {
            return null;
        }

        var versioned = Lifecycle(bucket).Versioning;
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        ArchiveVersion(conn, tx, bucket, key, versioned ? "noncurrent" : "deleted", softDeleted: !versioned);
        using (var delete = Query(conn, "DELETE FROM objects WHERE bucket = $b AND key = $k AND cid = $cid", ("$b", bucket), ("$k", key), ("$cid", record.Cid)))
        {
            delete.Transaction = tx;
            if (delete.ExecuteNonQuery() == 0)
            {
                return null; // replaced or deleted since the read
            }
        }

        if (versioned)
        {
            Run(conn, tx, "INSERT INTO object_versions (bucket, key, cid, size_bytes, kind, node_ids, created_at) VALUES ($b, $k, '', 0, 'marker', '', $now)",
                ("$b", bucket), ("$k", key), ("$now", Now()));
        }

        tx.Commit();
        return record;
    }

    /// <summary>Restores the newest soft-deleted or noncurrent version still in its recovery window, if the key is free.</summary>
    public ObjectRecord? RestoreLatestVersion(string bucket, string key)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        long? id = null;
        using (var find = Query(conn, """
                   SELECT id FROM object_versions WHERE bucket = $b AND key = $k AND kind IN ('deleted', 'noncurrent')
                     AND purge_queued_at IS NULL AND NOT EXISTS (SELECT 1 FROM objects WHERE bucket = $b AND key = $k)
                   ORDER BY created_at DESC LIMIT 1
                   """, ("$b", bucket), ("$k", key)))
        {
            find.Transaction = tx;
            id = find.ExecuteScalar() as long?;
        }

        if (id is null)
        {
            return null;
        }

        Run(conn, tx, """
            INSERT INTO objects (bucket, key, cid, size_bytes, content_type, last_modified, node_ids, replica_count, target_copies, locked_until, legal_hold,
                                 etag_override, regions, placement_gaps, merkle_root, merkle_cid, scheme)
            SELECT bucket, key, cid, size_bytes, COALESCE(content_type, 'application/octet-stream'), $now, node_ids,
                   CASE WHEN node_ids = '' THEN 0 ELSE length(node_ids) - length(replace(node_ids, ',', '')) + 1 END,
                   COALESCE(target_copies, CAST((SELECT value FROM settings WHERE name = 'copy_floor') AS INTEGER), 1), locked_until, legal_hold,
                   etag_override, regions, placement_gaps, merkle_root, merkle_cid, scheme
            FROM object_versions WHERE id = $id;
            DELETE FROM object_versions WHERE id = $id;
            DELETE FROM object_versions WHERE bucket = $b AND key = $k AND kind = 'marker';
            """, ("$now", Now()), ("$id", id), ("$b", bucket), ("$k", key));
        RefreshReplicationState(conn, tx, "WHERE bucket = $b AND key = $k", ("$b", bucket), ("$k", key));
        tx.Commit();
        return GetObject(bucket, key);
    }

    public IReadOnlyList<ObjectVersion> Versions(string bucket, string key)
    {
        using var conn = Open();
        return ReadVersions(Query(conn, $"SELECT {VersionColumns} FROM object_versions WHERE bucket = $b AND key = $k ORDER BY created_at, id",
            ("$b", bucket), ("$k", key)));
    }

    private const string VersionColumns =
        "id, bucket, key, cid, size_bytes, kind, node_ids, created_at, deleted_at, locked_until, legal_hold, physically_deleted_at";

    private static IReadOnlyList<ObjectVersion> ReadVersions(SqliteCommand cmd)
    {
        using (cmd)
        {
            using var r = cmd.ExecuteReader();
            var rows = new List<ObjectVersion>();
            while (r.Read())
            {
                rows.Add(new ObjectVersion(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
                    SplitNodeIds(r.GetString(6)), DateTimeOffset.Parse(r.GetString(7)), Time(r, 8), Time(r, 9), r.GetInt64(10) != 0, Time(r, 11)));
            }

            return rows;
        }
    }

    private static DateTimeOffset? Time(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i));

    // ── Lifecycle (#16 FR1.1, FR3) ────────────────────────────────────────────

    public BucketLifecycle Lifecycle(string bucket)
    {
        using var conn = Open();
        using var r = Query(conn, "SELECT versioning, max_versions, max_version_age_d, min_retention_d, recovery_d, multipart_ttl_h FROM bucket_lifecycle WHERE bucket = $b",
            ("$b", bucket)).ExecuteReader();
        return r.Read()
            ? new BucketLifecycle(r.GetInt64(0) != 0, Int(r, 1), Int(r, 2), Int(r, 3), Int(r, 4), Int(r, 5))
            : new BucketLifecycle();
    }

    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public void SetLifecycle(string bucket, BucketLifecycle rules)
    {
        using var conn = Open();
        Execute(Query(conn, """
            INSERT INTO bucket_lifecycle (bucket, versioning, max_versions, max_version_age_d, min_retention_d, recovery_d, multipart_ttl_h)
            VALUES ($b, $v, $mv, $ma, $mr, $rd, $mt)
            ON CONFLICT(bucket) DO UPDATE SET versioning = excluded.versioning, max_versions = excluded.max_versions,
                max_version_age_d = excluded.max_version_age_d, min_retention_d = excluded.min_retention_d,
                recovery_d = excluded.recovery_d, multipart_ttl_h = excluded.multipart_ttl_h
            """, ("$b", bucket), ("$v", rules.Versioning ? 1 : 0), ("$mv", rules.MaxVersions), ("$ma", rules.MaxVersionAgeDays),
            ("$mr", rules.MinRetentionDays), ("$rd", rules.RecoveryDays), ("$mt", rules.MultipartTtlHours)));
    }

    /// <summary>Buckets with versioning on (the version-expiry evaluator's work list).</summary>
    public IReadOnlyList<string> VersionedBuckets()
    {
        using var conn = Open();
        using var r = Query(conn, "SELECT bucket FROM bucket_lifecycle WHERE versioning = 1").ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    // ── Locks and legal holds (#16 FR2.4, FR7.2) ──────────────────────────────

    public bool SetRetention(string bucket, string key, DateTimeOffset? lockedUntil, bool legalHold)
    {
        using var conn = Open();
        return Execute(Query(conn, "UPDATE objects SET locked_until = $l, legal_hold = $h WHERE bucket = $b AND key = $k",
            ("$l", lockedUntil?.ToString("O")), ("$h", legalHold ? 1 : 0), ("$b", bucket), ("$k", key))) > 0;
    }

    /// <summary>Whether any object or retained version holding <paramref name="cid"/> is locked or on legal hold (FR7.2).</summary>
    public bool IsCidHeld(string cid)
    {
        using var conn = Open();
        return Query(conn, """
            SELECT 1 FROM objects WHERE cid = $c AND (legal_hold = 1 OR locked_until > $now)
            UNION ALL SELECT 1 FROM object_versions WHERE cid = $c AND physically_deleted_at IS NULL AND (legal_hold = 1 OR locked_until > $now)
            LIMIT 1
            """, ("$c", cid), ("$now", Now())).ExecuteScalar() is not null;
    }

    // ── Evaluators' queries ───────────────────────────────────────────────────

    /// <summary>Soft-deleted versions past <paramref name="cutoff"/>, not yet queued, not locked or held (FR2.1, FR2.4).</summary>
    /// <remarks>
    /// Each row is held to its own bucket's window in SQL, so a batch is never filled with long-window rows
    /// that aren't due while short-window ones wait behind them (review: Frank #10).
    /// </remarks>
    public IReadOnlyList<ObjectVersion> PurgeableVersions(DateTimeOffset now, int defaultRecoveryDays, int limit)
    {
        using var conn = Open();
        return ReadVersions(Query(conn, $"""
            SELECT {VersionColumns} FROM object_versions v
            WHERE purge_queued_at IS NULL AND deleted_at IS NOT NULL AND deleted_at < $cut
              AND julianday(deleted_at) <= julianday($now) - MIN(365, MAX(1, COALESCE((SELECT recovery_d FROM bucket_lifecycle l WHERE l.bucket = v.bucket), $default)))
              AND legal_hold = 0 AND (locked_until IS NULL OR locked_until <= $now)
            ORDER BY deleted_at LIMIT $l
            """, ("$cut", (now - TimeSpan.FromDays(1)).ToString("O")), ("$now", now.ToString("O")), ("$default", defaultRecoveryDays), ("$l", limit)));
    }

    /// <summary>Queues a version's bytes for physical deletion from every replica (FR2.2), unless something else still references them.</summary>
    public void QueueVersionPurge(ObjectVersion version)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        Run(conn, tx, "UPDATE object_versions SET purge_queued_at = $now WHERE id = $id", ("$now", Now()), ("$id", version.Id));
        // An erasure-coded version's bytes are its shards (#8): queue each, then drop the shard map.
        var shardCopies = new List<(string Cid, string Node)>();
        using (var read = Query(conn, "SELECT shard_cid, node_id FROM object_shards WHERE bucket = $b AND key = $k AND object_cid = $o",
                   ("$b", version.Bucket), ("$k", version.Key), ("$o", version.Cid)))
        {
            read.Transaction = tx;
            using var r = read.ExecuteReader();
            while (r.Read()) shardCopies.Add((r.GetString(0), r.GetString(1)));
        }

        if (shardCopies.Count > 0 && !LiveAt(conn, tx, version))
        {
            Run(conn, tx, "DELETE FROM object_shards WHERE bucket = $b AND key = $k AND object_cid = $o",
                ("$b", version.Bucket), ("$k", version.Key), ("$o", version.Cid));
            foreach (var (cid, node) in shardCopies)
            {
                Run(conn, tx, "INSERT OR IGNORE INTO gc_queue (cid, node_url, queued_at, category, priority) VALUES ($c, $n, $t, 'SoftDeletePurge', $p)",
                    ("$c", cid), ("$n", node), ("$t", Now()), ("$p", PriorityMedium));
                Run(conn, tx, "INSERT OR IGNORE INTO version_purges (version_id, cid) VALUES ($id, $c)", ("$id", version.Id), ("$c", cid));
            }

            tx.Commit();
            return;
        }

        if (version.Kind == "marker" || version.Cid.Length == 0)
        {
            Run(conn, tx, "UPDATE object_versions SET physically_deleted_at = $now WHERE id = $id", ("$now", Now()), ("$id", version.Id));
        }
        else
        {
            Run(conn, tx, "INSERT OR IGNORE INTO version_purges (version_id, cid) VALUES ($id, $c)", ("$id", version.Id), ("$c", version.Cid));
            foreach (var node in version.NodeIds)
            {
                Run(conn, tx, "INSERT OR IGNORE INTO gc_queue (cid, node_url, queued_at, category, priority) VALUES ($c, $n, $t, 'SoftDeletePurge', $p)",
                    ("$c", version.Cid), ("$n", node), ("$t", Now()), ("$p", PriorityMedium));
            }

            if (version.NodeIds.Count == 0)
            {
                CloseVersionsWaitingOn(conn, tx, version.Cid); // nothing to delete: physically gone now, billed until now (Frank #11)
            }
        }

        tx.Commit();
    }

    /// <summary>Whether the same version is live at the key again (restored or rewritten), so its bytes must stay.</summary>
    private static bool LiveAt(SqliteConnection conn, SqliteTransaction tx, ObjectVersion version)
    {
        using var check = Query(conn, "SELECT 1 FROM objects WHERE bucket = $b AND key = $k AND cid = $c", ("$b", version.Bucket), ("$k", version.Key), ("$c", version.Cid));
        check.Transaction = tx;
        return check.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Noncurrent versions of a key the lifecycle expires (FR3.1): beyond the newest <c>maxVersions</c> or older
    /// than <c>maxAge</c>, and never younger than the minimum retention (FR3.2).
    /// </summary>
    public IReadOnlyList<ObjectVersion> ExpiredVersions(string bucket, BucketLifecycle rules, DateTimeOffset now)
    {
        using var conn = Open();
        var all = ReadVersions(Query(conn, $"""
            SELECT {VersionColumns} FROM object_versions WHERE bucket = $b AND kind = 'noncurrent' AND deleted_at IS NULL
            ORDER BY key, created_at DESC
            """, ("$b", bucket)));
        var minRetention = TimeSpan.FromDays(rules.MinRetentionDays ?? 0);
        var expired = new List<ObjectVersion>();
        foreach (var byKey in all.GroupBy(v => v.Key))
        {
            var index = 0;
            foreach (var version in byKey)
            {
                index++;
                var tooMany = rules.MaxVersions is { } max && index > max;
                var tooOld = rules.MaxVersionAgeDays is { } days && now - version.CreatedAt > TimeSpan.FromDays(days);
                if ((tooMany || tooOld) && now - version.CreatedAt >= minRetention)
                {
                    expired.Add(version);
                }
            }
        }

        return expired;
    }

    public void SoftDeleteVersion(long id)
    {
        using var conn = Open();
        Execute(Query(conn, "UPDATE object_versions SET deleted_at = $now WHERE id = $id AND deleted_at IS NULL", ("$now", Now()), ("$id", id)));
    }

    /// <summary>Removes delete markers with no non-deleted version beneath them and no live object (FR3.3); returns how many.</summary>
    public int CleanOrphanedDeleteMarkers()
    {
        using var conn = Open();
        return Execute(Query(conn, """
            DELETE FROM object_versions AS m WHERE m.kind = 'marker'
              AND NOT EXISTS (SELECT 1 FROM object_versions v WHERE v.bucket = m.bucket AND v.key = m.key AND v.kind = 'noncurrent' AND v.deleted_at IS NULL)
              AND NOT EXISTS (SELECT 1 FROM objects o WHERE o.bucket = m.bucket AND o.key = m.key)
            """));
    }

    // ── Physical deletion bookkeeping (FR2.3, FR9) ────────────────────────────

    /// <summary>
    /// After a replica deletion was confirmed: when no copy of <paramref name="cid"/> is left queued, every
    /// version waiting on it is physically deleted, its bytes leave the tenant's usage, and
    /// <c>blob.physically_deleted</c> is recorded at that moment (billing stops then, FR9.1). Returns the versions closed.
    /// </summary>
    public int ConfirmReplicaDeleted(string cid)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var closed = CloseVersionsWaitingOn(conn, tx, cid);
        tx.Commit();
        return closed;
    }

    /// <summary>
    /// Closes every version waiting on <paramref name="cid"/> whose blobs have all left the queue: physically
    /// deleted now, its bytes out of the tenant's usage, and <c>blob.physically_deleted</c> recorded (same transaction).
    /// </summary>
    internal static int CloseVersionsWaitingOn(SqliteConnection conn, SqliteTransaction tx, string cid)
    {
        // Versions waiting on this blob whose every blob is now gone from the queue.
        var closed = new List<(long Id, string Bucket, string Key, long Size)>();
        using (var read = Query(conn, """
                   SELECT v.id, v.bucket, v.key, v.size_bytes FROM version_purges p JOIN object_versions v ON v.id = p.version_id
                   WHERE p.cid = $c AND v.physically_deleted_at IS NULL
                     AND NOT EXISTS (SELECT 1 FROM version_purges q JOIN gc_queue g ON g.cid = q.cid WHERE q.version_id = v.id)
                   """, ("$c", cid)))
        {
            read.Transaction = tx;
            using var r = read.ExecuteReader();
            while (r.Read()) closed.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
        }

        foreach (var (id, bucket, key, size) in closed)
        {
            Run(conn, tx, "UPDATE object_versions SET physically_deleted_at = $now WHERE id = $id", ("$now", Now()), ("$id", id));
            AdjustUsage(conn, tx, bucket, -size);
            Run(conn, tx, "INSERT INTO blob_events (type, bucket, key, cid, size_bytes, at) VALUES ('blob.physically_deleted', $b, $k, $c, $s, $now)",
                ("$b", bucket), ("$k", key), ("$c", cid), ("$s", size), ("$now", Now()));
        }

        return closed.Count;
    }

    public void RecordEvent(string type, string? bucket, string? key, string? cid, long? size)
    {
        using var conn = Open();
        Execute(Query(conn, "INSERT INTO blob_events (type, bucket, key, cid, size_bytes, at) VALUES ($t, $b, $k, $c, $s, $now)",
            ("$t", type), ("$b", bucket), ("$k", key), ("$c", cid), ("$s", size), ("$now", Now())));
    }

    public IReadOnlyList<(string Type, string? Bucket, string? Key, string? Cid, DateTimeOffset At)> Events(string type, int limit = 100)
    {
        using var conn = Open();
        using var r = Query(conn, "SELECT type, bucket, key, cid, at FROM blob_events WHERE type = $t ORDER BY id DESC LIMIT $l", ("$t", type), ("$l", limit)).ExecuteReader();
        var rows = new List<(string, string?, string?, string?, DateTimeOffset)>();
        while (r.Read())
        {
            rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                DateTimeOffset.Parse(r.GetString(4))));
        }

        return rows;
    }

    // ── Orphans (FR5) ─────────────────────────────────────────────────────────

    /// <summary>Records the first sighting of an unreferenced blob on a node; returns when it was first seen.</summary>
    public DateTimeOffset SeeOrphan(string cid, string nodeId, DateTimeOffset now)
    {
        using var conn = Open();
        Execute(Query(conn, "INSERT OR IGNORE INTO orphan_candidates (cid, node_id, first_seen_at) VALUES ($c, $n, $t)",
            ("$c", cid), ("$n", nodeId), ("$t", now.ToString("O"))));
        return DateTimeOffset.Parse((string)Query(conn, "SELECT first_seen_at FROM orphan_candidates WHERE cid = $c AND node_id = $n",
            ("$c", cid), ("$n", nodeId)).ExecuteScalar()!);
    }

    public void ForgetOrphan(string cid, string nodeId)
    {
        using var conn = Open();
        Execute(Query(conn, "DELETE FROM orphan_candidates WHERE cid = $c AND node_id = $n", ("$c", cid), ("$n", nodeId)));
    }

    // ── Offline replicas and placement warnings (FR4) ─────────────────────────

    public void MarkOffline(string bucket, string key, string cid, string nodeId)
    {
        using var conn = Open();
        Execute(Query(conn, "INSERT OR IGNORE INTO offline_replicas (bucket, key, cid, node_id, since) VALUES ($b, $k, $c, $n, $t)",
            ("$b", bucket), ("$k", key), ("$c", cid), ("$n", nodeId), ("$t", Now())));
    }

    public IReadOnlyList<string> OfflineReplicas(string bucket, string key)
    {
        using var conn = Open();
        using var r = Query(conn, "SELECT node_id FROM offline_replicas WHERE bucket = $b AND key = $k", ("$b", bucket), ("$k", key)).ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Re-replication confirmed: drop the Offline reference from the object and the mark (FR4.3).</summary>
    public void PurgeOfflineReplica(string bucket, string key, string cid, string nodeId)
    {
        RemoveReplica(bucket, key, cid, nodeId);
        using var conn = Open();
        Execute(Query(conn, "DELETE FROM offline_replicas WHERE bucket = $b AND key = $k AND node_id = $n", ("$b", bucket), ("$k", key), ("$n", nodeId)));
    }

    /// <summary>The node came back: it's no longer offline for any object.</summary>
    public int ClearOffline(string nodeId)
    {
        using var conn = Open();
        return Execute(Query(conn, "DELETE FROM offline_replicas WHERE node_id = $n", ("$n", nodeId)));
    }

    public void FlagPlacementWarning(string cid, string reason)
    {
        using var conn = Open();
        Execute(Query(conn, """
            INSERT INTO placement_warnings (cid, reason, first_flagged) VALUES ($c, $r, $t)
            ON CONFLICT(cid) DO UPDATE SET reason = excluded.reason, cleared_at = NULL
            """, ("$c", cid), ("$r", reason), ("$t", Now())));
    }

    public void ClearPlacementWarning(string cid)
    {
        using var conn = Open();
        Execute(Query(conn, "UPDATE placement_warnings SET cleared_at = $t WHERE cid = $c AND cleared_at IS NULL", ("$t", Now()), ("$c", cid)));
    }

    /// <summary>Open placement warnings, with the objects they affect, optionally limited to one tenant's buckets.</summary>
    public IReadOnlyList<(string Cid, string Reason, DateTimeOffset Since, string? Bucket, string? Key)> PlacementWarnings(string? tenantId = null)
    {
        using var conn = Open();
        using var r = Query(conn, """
            SELECT w.cid, w.reason, w.first_flagged, o.bucket, o.key FROM placement_warnings w
            LEFT JOIN objects o ON o.cid = w.cid
            LEFT JOIN buckets b ON b.name = o.bucket
            WHERE w.cleared_at IS NULL AND ($t IS NULL OR b.tenant_id = $t)
            """, ("$t", tenantId)).ExecuteReader();
        var rows = new List<(string, string, DateTimeOffset, string?, string?)>();
        while (r.Read())
        {
            rows.Add((r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        }

        return rows;
    }

    // ── Observability (FR10.1) ────────────────────────────────────────────────

    /// <summary>
    /// Versions waiting for physical deletion, per bucket (optionally one tenant's), with when the oldest
    /// becomes purgeable: soft-deleted ones at the end of their recovery window, queued ones now.
    /// </summary>
    public IReadOnlyList<(string Bucket, long Pending, long Queued, DateTimeOffset? OldestDeletedAt)> PendingDeletions(string? tenantId = null)
    {
        using var conn = Open();
        using var r = Query(conn, """
            SELECT v.bucket, COUNT(*), SUM(v.purge_queued_at IS NOT NULL), MIN(v.deleted_at)
            FROM object_versions v LEFT JOIN buckets b ON b.name = v.bucket
            WHERE v.physically_deleted_at IS NULL AND v.deleted_at IS NOT NULL AND ($t IS NULL OR b.tenant_id = $t)
            GROUP BY v.bucket
            """, ("$t", tenantId)).ExecuteReader();
        var rows = new List<(string, long, long, DateTimeOffset?)>();
        while (r.Read())
        {
            rows.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3))));
        }

        return rows;
    }
}
