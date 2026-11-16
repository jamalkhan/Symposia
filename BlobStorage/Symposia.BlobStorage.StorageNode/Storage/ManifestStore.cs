using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;

namespace Symposia.BlobStorage.StorageNode.Storage;

/// <summary>A change recorded in the manifest outbox (#12 Layer 1), streamed to gateways by WatchManifest.</summary>
public sealed record ManifestChange(long Seq, Cid Cid, bool Removed, long SizeBytes, BlobStatus Status, DateTimeOffset OccurredAt);

/// <summary>An epoch's manifest root as this node computed it, and whether it reached the chain.</summary>
public sealed record EpochRootRecord(ulong Epoch, byte[] Root, long LeafCount, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt, string? Outcome);

/// <summary>
/// Per-node local blob manifest (Layer 1 of Requirements/BlobStorage/metadata-architecture.md).
/// Embedded SQLite, not a central database — every node owns and serves only its own manifest.
/// </summary>
/// <remarks>
/// #12 adds three things on top of the original one-row-per-blob table:
/// <list type="bullet">
/// <item><c>blob_references</c>: who refers to each blob. Bytes are deduplicated by CID across tenants, so a
/// blob can have several (tenant, bucket, key) references; the legacy columns on <c>blob_manifest</c> keep the
/// last writer's, for N-1 readers.</item>
/// <item><c>manifest_events</c>: an outbox written in the same transaction as every row change, so the change
/// feed (WatchManifest) is at-least-once even across a crash between commit and broadcast.</item>
/// <item><c>epoch_roots</c>/<c>epoch_leaves</c>: the manifest as attested at each epoch boundary, kept for the
/// last two epochs so inclusion proofs against the on-chain root can be answered.</item>
/// </list>
/// </remarks>
public sealed class ManifestStore
{
    /// <summary>The tenant the gateway's RepairWorker writes copies under: a copy, never a new reference.</summary>
    public const string RepairTenant = "repair";

    private const int PruneEvery = 1000;

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly long _eventRetention;

    // Writes are serialised so the used-bytes counter (#197) applies each record's exact delta.
    private readonly object _writeGate = new();
    private long _usedBytes;
    private long _writeSeq;
    private int _eventsSincePrune;
    private TaskCompletionSource _changed = NewSignal();

    public ManifestStore(IOptions<StorageNodeOptions> options)
    {
        _dbPath = Path.GetFullPath(options.Value.ManifestDbPath);
        _eventRetention = Math.Max(1, options.Value.ManifestEventRetention);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
    }

    /// <summary>
    /// Set by <see cref="Initialize"/> when the database was missing or failed its integrity check, so the node
    /// must rebuild its manifest from the blobs on disk before serving (#12 FR1.4, AC2). A corrupt file is moved
    /// aside, never deleted.
    /// </summary>
    public bool NeedsRebuild { get; private set; }

    /// <summary>This manifest's change-feed incarnation; sequence numbers are only comparable within one.</summary>
    public string FeedId { get; private set; } = "";

    /// <summary>Where a corrupt database was moved, when one was.</summary>
    public string? QuarantinedPath { get; private set; }

    public void Initialize()
    {
        NeedsRebuild = !File.Exists(_dbPath) || !PassesIntegrityCheck();
        if (NeedsRebuild && File.Exists(_dbPath))
        {
            Quarantine();
        }

        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS blob_manifest (
                    cid                   TEXT PRIMARY KEY,
                    size_bytes            INTEGER NOT NULL,
                    tenant_id             TEXT NOT NULL,
                    bucket                TEXT NOT NULL,
                    key                   TEXT NOT NULL,
                    region_tags           TEXT NOT NULL,
                    stored_at             TEXT NOT NULL,
                    checksum_verified_at  TEXT NULL,
                    status                TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS blob_references (
                    cid        TEXT NOT NULL,
                    tenant_id  TEXT NOT NULL,
                    bucket     TEXT NOT NULL,
                    key        TEXT NOT NULL,
                    PRIMARY KEY (cid, tenant_id, bucket, key)
                );

                CREATE TABLE IF NOT EXISTS manifest_events (
                    seq          INTEGER PRIMARY KEY AUTOINCREMENT,
                    cid          TEXT NOT NULL,
                    kind         TEXT NOT NULL,   -- put | removed
                    size_bytes   INTEGER NOT NULL,
                    status       TEXT NOT NULL,
                    occurred_at  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS epoch_roots (
                    epoch         INTEGER PRIMARY KEY,
                    root          BLOB NOT NULL,
                    leaf_count    INTEGER NOT NULL,
                    created_at    TEXT NOT NULL,
                    submitted_at  TEXT NULL,
                    outcome       TEXT NULL       -- submitted | already-recorded | missed | conflict | rejected
                );

                CREATE TABLE IF NOT EXISTS epoch_leaves (
                    epoch       INTEGER NOT NULL,
                    cid         TEXT NOT NULL,
                    tenant_id   TEXT NOT NULL,
                    size_bytes  INTEGER NOT NULL,
                    PRIMARY KEY (epoch, cid, tenant_id)
                );

                CREATE INDEX IF NOT EXISTS idx_blob_manifest_status ON blob_manifest (status, cid);

                -- One row: the change feed's incarnation. A rebuilt manifest gets a new one, so a gateway whose cursor
                -- belongs to the old seq space resyncs instead of skipping the restarted sequence (review: Frank #1).
                CREATE TABLE IF NOT EXISTS manifest_meta (name TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT OR IGNORE INTO manifest_meta (name, value) VALUES ('feed_id', lower(hex(randomblob(16))));
                """;
            command.ExecuteNonQuery();
        }

        using (var feed = connection.CreateCommand())
        {
            feed.CommandText = "SELECT value FROM manifest_meta WHERE name = 'feed_id'";
            FeedId = (string)feed.ExecuteScalar()!;
        }

        BackfillReferences(connection);
        ReconcileUsedBytes();
    }

    /// <summary>References from before #12 lived only in the manifest row's own columns; copy them once.</summary>
    private static void BackfillReferences(SqliteConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        if ((long)(version.ExecuteScalar() ?? 0L) >= 1)
        {
            return;
        }

        using var tx = connection.BeginTransaction();
        using (var backfill = connection.CreateCommand())
        {
            backfill.Transaction = tx;
            backfill.CommandText =
                "INSERT OR IGNORE INTO blob_references (cid, tenant_id, bucket, key) " +
                "SELECT cid, tenant_id, bucket, key FROM blob_manifest WHERE tenant_id <> '' AND tenant_id <> $repair; " +
                "PRAGMA user_version = 1;";
            backfill.Parameters.AddWithValue("$repair", RepairTenant);
            backfill.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private bool PassesIntegrityCheck()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            return command.ExecuteScalar() as string == "ok";
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void Quarantine()
    {
        using (var pooled = new SqliteConnection(_connectionString))
        {
            SqliteConnection.ClearPool(pooled);
        }

        var aside = $"{_dbPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(_dbPath + suffix))
            {
                File.Move(_dbPath + suffix, aside + suffix, overwrite: true);
            }
        }

        QuarantinedPath = aside;
    }

    /// <summary>
    /// Bytes recorded in the manifest, kept in memory (issue #197) so Probe, heartbeats and the
    /// capacity monitor don't each run a SUM over the whole table. Seeded at <see cref="Initialize"/>
    /// and corrected by <see cref="ReconcileUsedBytes"/>.
    /// </summary>
    public long UsedBytes => Interlocked.Read(ref _usedBytes);

    /// <summary>
    /// Recomputes <see cref="UsedBytes"/> from the table; returns the drift it corrected. The SUM runs
    /// outside the write lock so writes aren't stalled by a full scan; if a write lands meanwhile, the
    /// counter (already exact for that write) is kept and the next reconcile tries again.
    /// </summary>
    public long ReconcileUsedBytes()
    {
        var seq = Interlocked.Read(ref _writeSeq);
        var actual = SumSizeBytes();
        lock (_writeGate)
        {
            return _writeSeq != seq ? 0 : actual - Interlocked.Exchange(ref _usedBytes, actual);
        }
    }

    public bool IsAccessible()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Upsert(BlobRecord record)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            var previous = SizeOf(connection, tx, record.Cid);
            UpsertRow(connection, tx, record);
            if (IsReference(record.TenantId))
            {
                Run(connection, tx, "INSERT OR IGNORE INTO blob_references (cid, tenant_id, bucket, key) VALUES ($cid, $t, $b, $k)",
                    ("$cid", record.Cid.Value), ("$t", record.TenantId), ("$b", record.Bucket), ("$k", record.Key));
            }

            AppendEvent(connection, tx, record.Cid, "put", record.SizeBytes, record.Status);
            tx.Commit();
            Interlocked.Add(ref _usedBytes, record.SizeBytes - (previous ?? 0));
            Interlocked.Increment(ref _writeSeq);
        }

        AfterChange();
    }

    private static bool IsReference(string tenantId) => tenantId.Length > 0 && tenantId != RepairTenant;

    private static long? SizeOf(SqliteConnection connection, SqliteTransaction tx, Cid cid)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT size_bytes FROM blob_manifest WHERE cid = $cid";
        command.Parameters.AddWithValue("$cid", cid.Value);
        return command.ExecuteScalar() is long size ? size : null;
    }

    private static void UpsertRow(SqliteConnection connection, SqliteTransaction tx, BlobRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        // A repair copy or an unattributed row (rebuild, orphan) never overwrites a known reference.
        command.CommandText =
            """
            INSERT INTO blob_manifest
                (cid, size_bytes, tenant_id, bucket, key, region_tags, stored_at, checksum_verified_at, status)
            VALUES
                ($cid, $size_bytes, $tenant_id, $bucket, $key, $region_tags, $stored_at, $checksum_verified_at, $status)
            ON CONFLICT(cid) DO UPDATE SET
                size_bytes = excluded.size_bytes,
                tenant_id = CASE WHEN $is_reference THEN excluded.tenant_id ELSE tenant_id END,
                bucket = CASE WHEN $is_reference THEN excluded.bucket ELSE bucket END,
                key = CASE WHEN $is_reference THEN excluded.key ELSE key END,
                region_tags = excluded.region_tags,
                stored_at = excluded.stored_at,
                checksum_verified_at = excluded.checksum_verified_at,
                status = excluded.status;
            """;

        command.Parameters.AddWithValue("$cid", record.Cid.Value);
        command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        command.Parameters.AddWithValue("$tenant_id", record.TenantId);
        command.Parameters.AddWithValue("$bucket", record.Bucket);
        command.Parameters.AddWithValue("$key", record.Key);
        command.Parameters.AddWithValue("$region_tags", string.Join(',', record.RegionTags));
        command.Parameters.AddWithValue("$stored_at", record.StoredAt.ToString("O"));
        command.Parameters.AddWithValue("$checksum_verified_at", (object?)record.ChecksumVerifiedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$is_reference", IsReference(record.TenantId));

        command.ExecuteNonQuery();
    }

    public BlobRecord? Get(Cid cid)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT size_bytes, tenant_id, bucket, key, region_tags, stored_at, checksum_verified_at, status " +
            "FROM blob_manifest WHERE cid = $cid";
        command.Parameters.AddWithValue("$cid", cid.Value);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return MapRecord(cid, reader);
    }

    /// <summary>Every (tenant, bucket, key) referring to <paramref name="cid"/>, in order.</summary>
    public IReadOnlyList<(string TenantId, string Bucket, string Key)> References(Cid cid)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tenant_id, bucket, key FROM blob_references WHERE cid = $cid ORDER BY tenant_id, bucket, key";
        command.Parameters.AddWithValue("$cid", cid.Value);
        using var reader = command.ExecuteReader();
        var references = new List<(string, string, string)>();
        while (reader.Read()) references.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return references;
    }

    public bool Delete(Cid cid)
    {
        bool deleted;
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            var previous = SizeOf(connection, tx, cid);
            deleted = Run(connection, tx, "DELETE FROM blob_manifest WHERE cid = $cid", ("$cid", cid.Value)) > 0;
            Run(connection, tx, "DELETE FROM blob_references WHERE cid = $cid", ("$cid", cid.Value));
            if (deleted)
            {
                AppendEvent(connection, tx, cid, "removed", 0, BlobStatus.Orphaned);
            }

            tx.Commit();
            if (deleted)
            {
                Interlocked.Add(ref _usedBytes, -(previous ?? 0));
                Interlocked.Increment(ref _writeSeq);
            }
        }

        if (deleted)
        {
            AfterChange();
        }

        return deleted;
    }

    public long CountBlobs()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM blob_manifest";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>Blobs whose status is Active: the holdings gossip reports and the epoch root attests (idx_blob_manifest_status).</summary>
    public long CountActive()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM blob_manifest WHERE status = $s";
        command.Parameters.AddWithValue("$s", nameof(BlobStatus.Active));
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public long SumSizeBytes()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM blob_manifest";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>Returns a page of CIDs ordered lexicographically, starting after <paramref name="afterCid"/>.</summary>
    public IReadOnlyList<string> ListCidsPaged(string afterCid, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = afterCid.Length == 0
            ? "SELECT cid FROM blob_manifest ORDER BY cid LIMIT $limit"
            : "SELECT cid FROM blob_manifest WHERE cid > $after ORDER BY cid LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        if (afterCid.Length > 0)
            command.Parameters.AddWithValue("$after", afterCid);

        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    /// <summary>A page of manifest rows as change-feed snapshot entries, by CID after <paramref name="afterCid"/>.</summary>
    public IReadOnlyList<ManifestChange> SnapshotPage(string afterCid, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT cid, size_bytes, status, stored_at FROM blob_manifest WHERE cid > $after ORDER BY cid LIMIT $limit";
        command.Parameters.AddWithValue("$after", afterCid);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var page = new List<ManifestChange>();
        while (reader.Read())
        {
            if (Cid.TryParse(reader.GetString(0), out var cid))
            {
                page.Add(new ManifestChange(0, cid, false, reader.GetInt64(1), Enum.Parse<BlobStatus>(reader.GetString(2)),
                    DateTimeOffset.Parse(reader.GetString(3))));
            }
        }

        return page;
    }

    /// <summary>Returns all blobs with the given status.</summary>
    public IReadOnlyList<BlobRecord> ListByStatus(BlobStatus status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT cid, size_bytes, tenant_id, bucket, key, region_tags, stored_at, checksum_verified_at, status " +
            "FROM blob_manifest WHERE status = $status";
        command.Parameters.AddWithValue("$status", status.ToString());

        using var reader = command.ExecuteReader();
        var results = new List<BlobRecord>();
        while (reader.Read())
        {
            if (Cid.TryParse(reader.GetString(0), out var cid))
                results.Add(MapRecord(cid, reader, offset: 1));
        }
        return results;
    }

    public void SetStatus(Cid cid, BlobStatus status)
    {
        bool changed;
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            changed = Run(connection, tx, "UPDATE blob_manifest SET status = $status WHERE cid = $cid AND status <> $status",
                ("$status", status.ToString()), ("$cid", cid.Value)) > 0;
            if (changed)
            {
                AppendEvent(connection, tx, cid, "put", SizeOf(connection, tx, cid) ?? 0, status);
            }

            tx.Commit();
        }

        if (changed)
        {
            AfterChange();
        }
    }

    public void UpdateChecksumVerifiedAt(Cid cid, DateTimeOffset verifiedAt)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE blob_manifest SET checksum_verified_at = $t WHERE cid = $cid";
        command.Parameters.AddWithValue("$t", verifiedAt.ToString("O"));
        command.Parameters.AddWithValue("$cid", cid.Value);
        command.ExecuteNonQuery();
    }

    // ── Change feed (#12 Layer 1 → Layer 2) ──────────────────────────────────

    /// <summary>The latest event sequence number; 0 when nothing has been recorded.</summary>
    public long HeadSeq()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM manifest_events";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// The oldest sequence number still kept. A reader whose cursor is below <c>this - 1</c> has missed pruned
    /// events and must start again from a snapshot.
    /// </summary>
    public long OldestSeq()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MIN(seq), 0) FROM manifest_events";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>Events after <paramref name="afterSeq"/>, oldest first.</summary>
    public IReadOnlyList<ManifestChange> EventsAfter(long afterSeq, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT seq, cid, kind, size_bytes, status, occurred_at FROM manifest_events WHERE seq > $after ORDER BY seq LIMIT $limit";
        command.Parameters.AddWithValue("$after", afterSeq);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var events = new List<ManifestChange>();
        while (reader.Read())
        {
            if (Cid.TryParse(reader.GetString(1), out var cid))
            {
                events.Add(new ManifestChange(reader.GetInt64(0), cid, reader.GetString(2) == "removed", reader.GetInt64(3),
                    Enum.Parse<BlobStatus>(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5))));
            }
        }

        return events;
    }

    /// <summary>The head seq and Active count read together, so a heartbeat's digest matches its cursor (review: Frank #4).</summary>
    public (long HeadSeq, long Active) Digest()
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            "SELECT (SELECT COALESCE(MAX(seq), 0) FROM manifest_events), (SELECT COUNT(*) FROM blob_manifest WHERE status = $s)";
        command.Parameters.AddWithValue("$s", nameof(BlobStatus.Active));
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Completes the next time the manifest changes (or immediately if it changed since this was last armed).</summary>
    public Task WaitForChangeAsync(CancellationToken ct) => Volatile.Read(ref _changed).Task.WaitAsync(ct);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void AfterChange() => Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();

    private void AppendEvent(SqliteConnection connection, SqliteTransaction tx, Cid cid, string kind, long size, BlobStatus status)
    {
        Run(connection, tx,
            "INSERT INTO manifest_events (cid, kind, size_bytes, status, occurred_at) VALUES ($cid, $kind, $size, $status, $at)",
            ("$cid", cid.Value), ("$kind", kind), ("$size", size), ("$status", status.ToString()), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        if (++_eventsSincePrune >= PruneEvery)
        {
            _eventsSincePrune = 0;
            Run(connection, tx, "DELETE FROM manifest_events WHERE seq <= (SELECT MAX(seq) FROM manifest_events) - $keep", ("$keep", _eventRetention));
        }
    }

    // ── Epoch roots (#12 Layer 3) ────────────────────────────────────────────

    /// <summary>
    /// Freezes the Active manifest as <paramref name="epoch"/>'s attested holdings and records its root; returns the
    /// existing record when the epoch was already frozen (a restart mid-epoch never recomputes a different root).
    /// Unreferenced Active blobs (rebuilt, or repair copies) are attested under tenant "".
    /// </summary>
    public EpochRootRecord FreezeEpoch(ulong epoch)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            if (ReadEpochRoot(connection, tx, epoch) is { } existing)
            {
                return existing;
            }

            Run(connection, tx,
                """
                INSERT INTO epoch_leaves (epoch, cid, tenant_id, size_bytes)
                SELECT $e, m.cid, COALESCE(r.tenant_id, ''), m.size_bytes
                FROM blob_manifest m LEFT JOIN (SELECT DISTINCT cid, tenant_id FROM blob_references) r ON r.cid = m.cid
                WHERE m.status = $active
                """, ("$e", (long)epoch), ("$active", nameof(BlobStatus.Active)));
            var leaves = ReadEpochLeaves(connection, tx, epoch);
            var record = new EpochRootRecord(epoch, ManifestMerkle.Root(leaves), leaves.Count, DateTimeOffset.UtcNow, null, null);
            Run(connection, tx, "INSERT INTO epoch_roots (epoch, root, leaf_count, created_at) VALUES ($e, $root, $n, $at)",
                ("$e", (long)epoch), ("$root", record.Root), ("$n", (long)record.LeafCount), ("$at", record.CreatedAt.ToString("O")));
            tx.Commit();
            return record;
        }
    }

    public EpochRootRecord? GetEpochRoot(ulong epoch)
    {
        using var connection = OpenConnection();
        return ReadEpochRoot(connection, null, epoch);
    }

    /// <summary>The newest epoch whose root reached the chain (submitted now, or found already recorded).</summary>
    public EpochRootRecord? LatestSubmittedEpoch()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(epoch) FROM epoch_roots WHERE submitted_at IS NOT NULL";
        return command.ExecuteScalar() is long epoch ? ReadEpochRoot(connection, null, (ulong)epoch) : null;
    }

    /// <summary>The attested leaves of a frozen epoch, in canonical order.</summary>
    public IReadOnlyList<ManifestLeaf> EpochLeaves(ulong epoch)
    {
        using var connection = OpenConnection();
        return ReadEpochLeaves(connection, null, epoch);
    }

    /// <summary>A frozen epoch's leaves, leaf hashes and CID positions, for answering proofs without rebuilding them per request.</summary>
    public sealed record EpochProofIndex(ulong Epoch, IReadOnlyList<ManifestLeaf> Leaves, IReadOnlyList<byte[]> Hashes, IReadOnlyDictionary<string, int> IndexOf);

    private EpochProofIndex? _proofIndex;

    /// <summary>
    /// The proof index for <paramref name="epoch"/>, built once and cached (frozen epochs never change), so a proof costs
    /// one sibling walk, not a whole tree (review: Carl #3).
    /// </summary>
    public EpochProofIndex ProofIndex(ulong epoch)
    {
        if (Volatile.Read(ref _proofIndex) is { } cached && cached.Epoch == epoch)
        {
            return cached;
        }

        var leaves = EpochLeaves(epoch);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < leaves.Count; i++)
        {
            index.TryAdd(leaves[i].Cid.Value, i); // a CID's first leaf (several tenants may share it)
        }

        var built = new EpochProofIndex(epoch, leaves, ManifestMerkle.LeafHashes(leaves), index);
        Volatile.Write(ref _proofIndex, built);
        return built;
    }

    /// <summary>Records how submitting <paramref name="epoch"/>'s root ended; <paramref name="onChain"/> when the chain holds it.</summary>
    public void MarkEpoch(ulong epoch, string outcome, bool onChain)
    {
        using var connection = OpenConnection();
        Run(connection, null, "UPDATE epoch_roots SET outcome = $o, submitted_at = CASE WHEN $on THEN $at ELSE submitted_at END WHERE epoch = $e",
            ("$o", outcome), ("$on", onChain), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$e", (long)epoch));
    }

    /// <summary>Drops frozen epochs before <paramref name="keepFrom"/>; their roots stay on-chain, only proofs for them end.</summary>
    public void PruneEpochs(ulong keepFrom)
    {
        using var connection = OpenConnection();
        Run(connection, null, "DELETE FROM epoch_leaves WHERE epoch < $e; DELETE FROM epoch_roots WHERE epoch < $e;", ("$e", (long)keepFrom));
    }

    private static EpochRootRecord? ReadEpochRoot(SqliteConnection connection, SqliteTransaction? tx, ulong epoch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT root, leaf_count, created_at, submitted_at, outcome FROM epoch_roots WHERE epoch = $e";
        command.Parameters.AddWithValue("$e", (long)epoch);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new EpochRootRecord(epoch, (byte[])reader.GetValue(0), reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }

    private static List<ManifestLeaf> ReadEpochLeaves(SqliteConnection connection, SqliteTransaction? tx, ulong epoch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT cid, tenant_id, size_bytes FROM epoch_leaves WHERE epoch = $e";
        command.Parameters.AddWithValue("$e", (long)epoch);
        using var reader = command.ExecuteReader();
        var leaves = new List<ManifestLeaf>();
        while (reader.Read())
        {
            if (Cid.TryParse(reader.GetString(0), out var cid))
            {
                leaves.Add(new ManifestLeaf(cid, reader.GetInt64(2), reader.GetString(1)));
            }
        }

        // The canonical order is ordinal, which SQLite's default collation doesn't promise for every tenant ID.
        return ManifestMerkle.Sort(leaves);
    }

    private static BlobRecord MapRecord(Cid cid, SqliteDataReader reader, int offset = 0)
    {
        // offset: columns before size_bytes (ListByStatus selects cid first).
        int Col(int i) => i + offset;

        var regionTagsRaw = reader.GetString(Col(4));
        var regionTags = regionTagsRaw.Length == 0
            ? Array.Empty<string>()
            : regionTagsRaw.Split(',');

        return new BlobRecord(
            cid,
            reader.GetInt64(Col(0)),
            reader.GetString(Col(1)),
            reader.GetString(Col(2)),
            reader.GetString(Col(3)),
            regionTags,
            DateTimeOffset.Parse(reader.GetString(Col(5))),
            reader.IsDBNull(Col(6)) ? null : DateTimeOffset.Parse(reader.GetString(Col(6))),
            Enum.Parse<BlobStatus>(reader.GetString(Col(7))));
    }

    private static int Run(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
