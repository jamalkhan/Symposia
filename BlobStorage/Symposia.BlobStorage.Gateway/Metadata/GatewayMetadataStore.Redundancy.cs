using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>
/// Redundancy metadata (#8): placement constraint gaps and Merkle roots on objects, folder-level region
/// rules, the repair-task audit log behind SLA measurement, pending pre-replication records, and the shard
/// map of erasure-coded objects.
/// </summary>
public sealed partial class GatewayMetadataStore
{
    private static void InitializeRedundancy(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        AddColumnIfMissing(conn, tx, "objects", "placement_gaps", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "objects", "merkle_root", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "objects", "scheme", "TEXT NOT NULL DEFAULT 'replica'");
        AddColumnIfMissing(conn, tx, "buckets", "scheme", "TEXT NULL");
        Run(conn, tx, """
            -- Folder-level region assignment (#8 FR1.1): the longest matching prefix wins over the bucket's.
            CREATE TABLE IF NOT EXISTS region_rules (
                bucket  TEXT NOT NULL,
                prefix  TEXT NOT NULL,
                regions TEXT NOT NULL,
                PRIMARY KEY (bucket, prefix)
            );

            -- Every repair from trigger to completion (#8 FR4.5): the live queue is in memory, this is the
            -- audit trail SLA compliance is measured from and in-flight work is re-hydrated from on restart.
            CREATE TABLE IF NOT EXISTS repair_tasks (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                cid           TEXT NOT NULL,
                priority      INTEGER NOT NULL,
                reason        TEXT NOT NULL,
                bad_node      TEXT NULL,
                triggered_at  TEXT NOT NULL,
                completed_at  TEXT NULL,
                outcome       TEXT NULL,       -- Repaired | Skipped | Failed | Superseded
                superseded_by INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS idx_repair_tasks_open ON repair_tasks (priority, triggered_at) WHERE completed_at IS NULL;
            CREATE INDEX IF NOT EXISTS idx_repair_tasks_cid_open ON repair_tasks (cid) WHERE completed_at IS NULL;
            CREATE INDEX IF NOT EXISTS idx_repair_tasks_completed ON repair_tasks (priority, completed_at) WHERE completed_at IS NOT NULL;

            -- Penalty pre-replication (#8 FR3.2): extra copies asked for because a holder entered a penalty stage.
            CREATE TABLE IF NOT EXISTS pending_replicas (
                cid          TEXT NOT NULL,
                node_id      TEXT NOT NULL,   -- the penalized node that triggered it
                stage        INTEGER NOT NULL,
                extra        INTEGER NOT NULL,
                created_at   TEXT NOT NULL,
                completed_at TEXT NULL,
                PRIMARY KEY (cid, node_id)
            );

            -- Erasure-coded objects (#8 FR5): one row per shard. objects.node_ids stays empty for them.
            CREATE TABLE IF NOT EXISTS object_shards (
                bucket      TEXT NOT NULL,
                key         TEXT NOT NULL,
                object_cid  TEXT NOT NULL,
                shard_index INTEGER NOT NULL,
                shard_cid   TEXT NOT NULL,
                node_id     TEXT NOT NULL,
                PRIMARY KEY (bucket, key, object_cid, shard_index)
            );
            CREATE INDEX IF NOT EXISTS idx_object_shards_cid ON object_shards (shard_cid);
            CREATE INDEX IF NOT EXISTS idx_object_shards_node ON object_shards (node_id);
            CREATE INDEX IF NOT EXISTS idx_objects_erasure ON objects (bucket, key) WHERE scheme <> 'replica';
            """);
        tx.Commit();
    }

    // ── Region assignment by folder (#8 FR1.1) ────────────────────────────────

    public void SetPrefixRegions(string bucket, string prefix, IReadOnlyList<string> regions)
    {
        using var conn = Open();
        Execute(regions.Count == 0
            ? Query(conn, "DELETE FROM region_rules WHERE bucket = $b AND prefix = $p", ("$b", bucket), ("$p", prefix))
            : Query(conn, "INSERT INTO region_rules (bucket, prefix, regions) VALUES ($b, $p, $r) ON CONFLICT(bucket, prefix) DO UPDATE SET regions = excluded.regions",
                ("$b", bucket), ("$p", prefix), ("$r", string.Join(',', regions))));
    }

    /// <summary>The bucket's folder rules (few per bucket: the resolver matches them in memory).</summary>
    public IReadOnlyList<(string Prefix, IReadOnlyList<string> Regions)> PrefixRegions(string bucket)
    {
        using var conn = Open();
        using var r = Query(conn, "SELECT prefix, regions FROM region_rules WHERE bucket = $b", ("$b", bucket)).ExecuteReader();
        var rules = new List<(string, IReadOnlyList<string>)>();
        while (r.Read())
        {
            rules.Add((r.GetString(0), SplitList(r.GetString(1))));
        }

        return rules;
    }

    /// <summary>A uniformly random full-replica object, via a rowid seek (no scan); null when there are none.</summary>
    public ObjectRecord? RandomObject()
    {
        using var conn = Open();
        using var cmd = Query(conn, $"""
            SELECT {ObjectColumns} FROM objects
            WHERE rowid >= (abs(random()) % (SELECT COALESCE(MAX(rowid), 0) + 1 FROM objects)) AND scheme = 'replica' AND replica_count > 0
            ORDER BY rowid LIMIT 1
            """);
        return ReadRecords(cmd).FirstOrDefault() ?? ListAllObjectsPaged(null, 1).FirstOrDefault(r => !r.IsErasureCoded && r.NodeIds.Count > 0);
    }

    // ── Erasure coding scheme per bucket (#8 FR5.1) ───────────────────────────

    public string? BucketScheme(string bucket)
    {
        using var conn = Open();
        return Query(conn, "SELECT scheme FROM buckets WHERE name = $n", ("$n", bucket)).ExecuteScalar() as string;
    }

    public bool SetBucketScheme(string bucket, string? scheme)
    {
        using var conn = Open();
        return Execute(Query(conn, "UPDATE buckets SET scheme = $s WHERE name = $n", ("$s", scheme), ("$n", bucket))) > 0;
    }

    // ── Repair task audit (#8 FR4.5) ──────────────────────────────────────────

    /// <summary>Opens a task row; an open task for the same CID at a lower urgency is superseded by it (never downgraded).</summary>
    public long OpenRepairTask(string cid, int priority, string reason, string? badNode, DateTimeOffset triggeredAt)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        long id;
        using (var insert = Query(conn, """
                   INSERT INTO repair_tasks (cid, priority, reason, bad_node, triggered_at) VALUES ($c, $p, $r, $b, $t);
                   SELECT last_insert_rowid();
                   """, ("$c", cid), ("$p", priority), ("$r", reason), ("$b", badNode), ("$t", triggeredAt.ToString("O"))))
        {
            insert.Transaction = tx;
            id = (long)insert.ExecuteScalar()!;
        }

        Run(conn, tx, """
            UPDATE repair_tasks SET completed_at = $t, outcome = 'Superseded', superseded_by = $id
            WHERE cid = $c AND completed_at IS NULL AND id <> $id
            """, ("$t", triggeredAt.ToString("O")), ("$id", id), ("$c", cid));
        tx.Commit();
        return id;
    }

    public void CloseRepairTask(long id, string outcome)
    {
        using var conn = Open();
        Execute(Query(conn, "UPDATE repair_tasks SET completed_at = $t, outcome = $o WHERE id = $id AND completed_at IS NULL",
            ("$t", Now()), ("$o", outcome), ("$id", id)));
    }

    public sealed record OpenRepair(long Id, string Cid, int Priority, string Reason, string? BadNode, DateTimeOffset TriggeredAt);

    /// <summary>Tasks still open, most urgent first (re-hydrated into the live queue on restart).</summary>
    public IReadOnlyList<OpenRepair> OpenRepairTasks(int limit)
    {
        using var conn = Open();
        using var r = Query(conn, """
            SELECT id, cid, priority, reason, bad_node, triggered_at FROM repair_tasks
            WHERE completed_at IS NULL ORDER BY priority, triggered_at LIMIT $l
            """, ("$l", limit)).ExecuteReader();
        var rows = new List<OpenRepair>();
        while (r.Read())
        {
            rows.Add(new OpenRepair(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                DateTimeOffset.Parse(r.GetString(5))));
        }

        return rows;
    }

    /// <summary>Per tier since <paramref name="since"/>: tasks completed, completed within <paramref name="slas"/>, and still open past it.</summary>
    public IReadOnlyList<(int Priority, long Completed, long WithinSla, long OpenOverdue)> RepairSlaReport(DateTimeOffset since, IReadOnlyList<TimeSpan> slas)
    {
        using var conn = Open();
        var rows = new List<(int, long, long, long)>();
        for (var p = 0; p < slas.Count; p++)
        {
            using var r = Query(conn, """
                SELECT COUNT(*),
                       COALESCE(SUM((julianday(completed_at) - julianday(triggered_at)) * 86400.0 <= $sla), 0)
                FROM repair_tasks WHERE priority = $p AND completed_at IS NOT NULL AND completed_at >= $since AND outcome <> 'Superseded'
                """, ("$p", p), ("$sla", slas[p].TotalSeconds), ("$since", since.ToString("O"))).ExecuteReader();
            r.Read();
            var completed = r.GetInt64(0);
            var within = r.GetInt64(1);
            var overdue = (long)Query(conn, "SELECT COUNT(*) FROM repair_tasks WHERE priority = $p AND completed_at IS NULL AND triggered_at < $cut",
                ("$p", p), ("$cut", (DateTimeOffset.UtcNow - slas[p]).ToString("O"))).ExecuteScalar()!;
            rows.Add((p, completed, within, overdue));
        }

        return rows;
    }

    // ── Pending replicas (#8 FR3.2) ───────────────────────────────────────────

    /// <summary>Records extra copies asked for <paramref name="cid"/> because <paramref name="nodeId"/> is penalized; false when already recorded.</summary>
    public bool AddPendingReplica(string cid, string nodeId, int stage, int extra)
    {
        using var conn = Open();
        return Execute(Query(conn, """
            INSERT INTO pending_replicas (cid, node_id, stage, extra, created_at) VALUES ($c, $n, $s, $e, $t)
            ON CONFLICT(cid, node_id) DO UPDATE SET stage = excluded.stage, extra = MAX(extra, excluded.extra), completed_at = NULL
            WHERE excluded.stage > pending_replicas.stage
            """, ("$c", cid), ("$n", nodeId), ("$s", stage), ("$e", extra), ("$t", Now()))) > 0;
    }

    /// <summary>Marks pending replicas done for objects that now hold their raised target.</summary>
    public int CompletePendingReplicas(string cid)
    {
        using var conn = Open();
        return Execute(Query(conn, "UPDATE pending_replicas SET completed_at = $t WHERE cid = $c AND completed_at IS NULL", ("$t", Now()), ("$c", cid)));
    }

    public IReadOnlyList<(string NodeId, int Stage, int Extra, bool Done)> PendingReplicas(string cid)
    {
        using var conn = Open();
        using var r = Query(conn, "SELECT node_id, stage, extra, completed_at IS NOT NULL FROM pending_replicas WHERE cid = $c", ("$c", cid)).ExecuteReader();
        var rows = new List<(string, int, int, bool)>();
        while (r.Read())
        {
            rows.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetBoolean(3)));
        }

        return rows;
    }

    /// <summary>Objects with a replica on <paramref name="nodeId"/> (a scan: used on rare penalty-stage changes only).</summary>
    public IReadOnlyList<ObjectRecord> ObjectsOnNode(string nodeId, int limit, (string Bucket, string Key)? after = null)
    {
        using var conn = Open();
        using var cmd = Query(conn,
            $"SELECT {ObjectColumns} FROM objects WHERE instr(',' || node_ids || ',', ',' || $n || ',') > 0 AND (bucket, key) > ($b, $k) ORDER BY bucket, key LIMIT $l",
            ("$n", nodeId), ("$b", after?.Bucket ?? ""), ("$k", after?.Key ?? ""), ("$l", limit));
        return ReadRecords(cmd);
    }

    // ── Erasure-coded shards (#8 FR5) ─────────────────────────────────────────

    public sealed record Shard(int Index, string Cid, string NodeId);

    /// <summary>The shards of the object version <paramref name="objectCid"/> at the key (the current one when null).</summary>
    public IReadOnlyList<Shard> Shards(string bucket, string key, string? objectCid = null)
    {
        using var conn = Open();
        using var r = Query(conn, """
            SELECT shard_index, shard_cid, node_id FROM object_shards
            WHERE bucket = $b AND key = $k AND object_cid = COALESCE($o, (SELECT cid FROM objects WHERE bucket = $b AND key = $k))
            ORDER BY shard_index
            """, ("$b", bucket), ("$k", key), ("$o", objectCid)).ExecuteReader();
        var shards = new List<Shard>();
        while (r.Read())
        {
            shards.Add(new Shard(r.GetInt32(0), r.GetString(1), r.GetString(2)));
        }

        return shards;
    }

    /// <summary>Stores (or moves) one shard of an object, if the key still holds <paramref name="objectCid"/>; the replaced shard copy is queued for GC.</summary>
    public bool PutShard(string bucket, string key, string objectCid, Shard shard)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        using (var check = Query(conn, "SELECT 1 FROM objects WHERE bucket = $b AND key = $k AND cid = $c", ("$b", bucket), ("$k", key), ("$c", objectCid)))
        {
            check.Transaction = tx;
            if (check.ExecuteScalar() is null)
            {
                return false;
            }
        }

        (string Cid, string Node)? old = null;
        using (var read = Query(conn, "SELECT shard_cid, node_id FROM object_shards WHERE bucket = $b AND key = $k AND shard_index = $i AND object_cid = $o",
                   ("$b", bucket), ("$k", key), ("$i", shard.Index), ("$o", objectCid)))
        {
            read.Transaction = tx;
            using var r = read.ExecuteReader();
            if (r.Read()) old = (r.GetString(0), r.GetString(1));
        }

        Run(conn, tx, """
            INSERT INTO object_shards (bucket, key, object_cid, shard_index, shard_cid, node_id) VALUES ($b, $k, $o, $i, $c, $n)
            ON CONFLICT(bucket, key, object_cid, shard_index) DO UPDATE SET shard_cid = excluded.shard_cid, node_id = excluded.node_id
            """, ("$b", bucket), ("$k", key), ("$o", objectCid), ("$i", shard.Index), ("$c", shard.Cid), ("$n", shard.NodeId));
        CancelPendingDeletion(conn, tx, shard.Cid);
        if (old is { } o && (o.Cid != shard.Cid || o.Node != shard.NodeId))
        {
            CollectIfUnreferenced(conn, tx, o.Cid, [o.Node]);
        }

        tx.Commit();
        return true;
    }

    /// <summary>Commits an erasure-coded object and its shard map in one transaction, so a reader never sees one without the other.</summary>
    public CommitOutcome CommitErasureObject(ObjectRecord record, IReadOnlyList<Shard> shards)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var outcome = UpsertObject(conn, tx, record);
        if (outcome.Applied)
        {
            Run(conn, tx, "DELETE FROM object_shards WHERE bucket = $b AND key = $k AND object_cid = $o", ("$b", record.Bucket), ("$k", record.Key), ("$o", record.Cid));
            foreach (var shard in shards)
            {
                Run(conn, tx, "INSERT INTO object_shards (bucket, key, object_cid, shard_index, shard_cid, node_id) VALUES ($b, $k, $o, $i, $c, $n)",
                    ("$b", record.Bucket), ("$k", record.Key), ("$o", record.Cid), ("$i", shard.Index), ("$c", shard.Cid), ("$n", shard.NodeId));
                CancelPendingDeletion(conn, tx, shard.Cid);
            }
        }
        else
        {
            foreach (var shard in shards)
            {
                CollectIfUnreferenced(conn, tx, shard.Cid, [shard.NodeId]);
            }
        }

        tx.Commit();
        return outcome;
    }

    /// <summary>Erasure-coded objects, paged by key (the EC repair scan).</summary>
    public IReadOnlyList<ObjectRecord> ListErasureObjectsPaged((string Bucket, string Key)? after, int limit)
    {
        using var conn = Open();
        using var cmd = Query(conn, $"SELECT {ObjectColumns} FROM objects WHERE scheme <> 'replica' AND (bucket, key) > ($b, $k) ORDER BY bucket, key LIMIT $l",
            ("$b", after?.Bucket ?? ""), ("$k", after?.Key ?? ""), ("$l", limit));
        return ReadRecords(cmd);
    }

    /// <summary>Removes an object's shard rows and queues every shard copy no longer referenced (same transaction).</summary>
    internal static void CollectShards(SqliteConnection conn, SqliteTransaction tx, string bucket, string key, string objectCid)
    {
        var shards = new List<(string Cid, string Node)>();
        using (var read = Query(conn, "SELECT shard_cid, node_id FROM object_shards WHERE bucket = $b AND key = $k AND object_cid = $o",
                   ("$b", bucket), ("$k", key), ("$o", objectCid)))
        {
            read.Transaction = tx;
            using var r = read.ExecuteReader();
            while (r.Read()) shards.Add((r.GetString(0), r.GetString(1)));
        }

        Run(conn, tx, "DELETE FROM object_shards WHERE bucket = $b AND key = $k AND object_cid = $o", ("$b", bucket), ("$k", key), ("$o", objectCid));
        foreach (var (cid, node) in shards)
        {
            CollectIfUnreferenced(conn, tx, cid, [node]);
        }
    }

    private static string? GapsJson(IReadOnlyList<PlacementGapRecord> gaps) => gaps.Count == 0 ? null : JsonSerializer.Serialize(gaps);

    private static IReadOnlyList<PlacementGapRecord> ReadGaps(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<PlacementGapRecord>>(json) ?? [];

    /// <summary>Replaces an object's recorded placement gaps (P3 re-evaluation, #8 FR2.4).</summary>
    public void SetPlacementGaps(string bucket, string key, string cid, IReadOnlyList<PlacementGapRecord> gaps)
    {
        using var conn = Open();
        Execute(Query(conn, "UPDATE objects SET placement_gaps = $g WHERE bucket = $b AND key = $k AND cid = $c",
            ("$g", GapsJson(gaps)), ("$b", bucket), ("$k", key), ("$c", cid)));
    }
}

/// <summary>A placement constraint gap as stored with the object (#8 FR2.3).</summary>
public sealed record PlacementGapRecord(string Rule, string Detail);
