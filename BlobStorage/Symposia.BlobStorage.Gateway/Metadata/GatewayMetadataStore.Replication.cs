using Microsoft.Data.Sqlite;

namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>
/// Replication state, per-object copy targets and bucket region assignments (#7). An object is committed
/// once its write quorum confirms; the copies past quorum land afterwards and are added to
/// <c>node_ids</c> one by one, so read routing only ever sees confirmed replicas (#7 FR6), and the row
/// flips to <see cref="ReplicationState.Full"/> when <c>replica_count</c> reaches <c>target_copies</c>.
/// </summary>
public sealed partial class GatewayMetadataStore
{
    private static void InitializeReplication(SqliteConnection conn, int minCopies)
    {
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        AddColumnIfMissing(conn, tx, "buckets", "regions", "TEXT NULL");
        // Rows from before #7 were written to every writable node at once: fully replicated, held to the floor.
        AddColumnIfMissing(conn, tx, "objects", "replication_state", "TEXT NOT NULL DEFAULT 'full'");
        AddColumnIfMissing(conn, tx, "objects", "target_copies", $"INTEGER NOT NULL DEFAULT {Math.Max(1, minCopies)}");
        AddColumnIfMissing(conn, tx, "objects", "regions", "TEXT NULL");
        // "Which objects have fewer copies than their own target", in fewest-missing-first order: the
        // expression matches ListShortOfTarget's predicate exactly, so it range-scans this index.
        Run(conn, tx, """
            CREATE INDEX IF NOT EXISTS idx_objects_short ON objects ((target_copies - replica_count), bucket, key)
                WHERE replica_count >= 1 AND target_copies > replica_count;
            CREATE TABLE IF NOT EXISTS settings (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);

        // The copy floor applies to every object: when the operator raises MinCopiesPerObject, lift older
        // rows to it once (a full pass, but only on the start after the change), never lowering any target.
        string? applied;
        using (var read = Query(conn, "SELECT value FROM settings WHERE name = 'copy_floor'"))
        {
            read.Transaction = tx;
            applied = read.ExecuteScalar() as string;
        }

        var floor = Math.Max(1, minCopies);
        if (applied != floor.ToString())
        {
            Run(conn, tx, "UPDATE objects SET target_copies = $f WHERE target_copies < $f", ("$f", floor));
            RefreshReplicationState(conn, tx, "WHERE replica_count < target_copies OR replication_state = 'quorum'");
            Run(conn, tx, "INSERT INTO settings (name, value) VALUES ('copy_floor', $f) ON CONFLICT(name) DO UPDATE SET value = excluded.value",
                ("$f", floor.ToString()));
        }

        tx.Commit();
    }

    /// <summary>The regions a bucket's objects are assigned to (empty = global).</summary>
    public IReadOnlyList<string> BucketRegions(string bucket)
    {
        using var conn = Open();
        return SplitList(Query(conn, "SELECT regions FROM buckets WHERE name = $n", ("$n", bucket)).ExecuteScalar() as string);
    }

    /// <summary>Sets a bucket's region assignment; false when the bucket doesn't exist.</summary>
    public bool SetBucketRegions(string bucket, IReadOnlyList<string> regions)
    {
        using var conn = Open();
        return Execute(Query(conn, "UPDATE buckets SET regions = $r WHERE name = $n",
            ("$r", regions.Count == 0 ? null : string.Join(',', regions)), ("$n", bucket))) > 0;
    }

    /// <summary>
    /// Records that <paramref name="nodeId"/> durably confirmed its copy after quorum (#7 FR5), when the key
    /// still holds <paramref name="cid"/>; the row becomes fully replicated once it reaches its target.
    /// </summary>
    public bool AddConfirmedReplica(string bucket, string key, string cid, string nodeId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var changed = ModifyNodeIds(conn, tx, "WHERE bucket = $b AND key = $k AND cid = $cid",
            [("$b", bucket), ("$k", key), ("$cid", cid)],
            ids => ids.Contains(nodeId, StringComparer.OrdinalIgnoreCase) ? ids : [.. ids, nodeId]) > 0;
        RefreshReplicationState(conn, tx, "WHERE bucket = $b AND key = $k", ("$b", bucket), ("$k", key));
        tx.Commit();
        return changed;
    }

    /// <summary>Recomputes replication_state from replica_count for the matching rows (same transaction).</summary>
    internal static void RefreshReplicationState(SqliteConnection conn, SqliteTransaction tx, string where, params (string, object?)[] args) =>
        Run(conn, tx, $"UPDATE objects SET replication_state = CASE WHEN replica_count >= target_copies THEN 'full' ELSE 'quorum' END {where}", args);

    /// <summary>
    /// Up to <paramref name="limit"/> repairable objects (at least one copy) below their own target, fewest
    /// missing first, after the cursor. Backed by idx_objects_short; replaces the global-floor sweep now
    /// that targets differ per bucket region assignment (#7 integration note).
    /// </summary>
    public IReadOnlyList<ObjectRecord> ListShortOfTarget(int limit, (int Missing, string Bucket, string Key)? after = null)
    {
        using var conn = Open();
        const string select = $"SELECT {ObjectColumns} FROM objects WHERE replica_count >= 1 AND target_copies > replica_count";
        using var cmd = after is null
            ? Query(conn, select + " ORDER BY (target_copies - replica_count), bucket, key LIMIT $limit", ("$limit", limit))
            : Query(conn, select + " AND ((target_copies - replica_count), bucket, key) > ($m, $b, $k) ORDER BY (target_copies - replica_count), bucket, key LIMIT $limit",
                ("$m", after.Value.Missing), ("$b", after.Value.Bucket), ("$k", after.Value.Key), ("$limit", limit));
        return ReadRecords(cmd);
    }

    /// <summary>Raises (never lowers) an object's copy target, e.g. for penalty pre-replication (#8 FR3).</summary>
    public bool RaiseTargetCopies(string bucket, string key, string cid, int target)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        int changed;
        using (var update = Query(conn, "UPDATE objects SET target_copies = $t WHERE bucket = $b AND key = $k AND cid = $cid AND target_copies < $t",
                   ("$t", target), ("$b", bucket), ("$k", key), ("$cid", cid)))
        {
            update.Transaction = tx;
            changed = update.ExecuteNonQuery();
        }

        RefreshReplicationState(conn, tx, "WHERE bucket = $b AND key = $k", ("$b", bucket), ("$k", key));
        tx.Commit();
        return changed > 0;
    }

    private static IReadOnlyList<string> SplitList(string? raw) =>
        string.IsNullOrEmpty(raw) ? [] : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public enum ReplicationState
{
    /// <summary>Committed at write quorum; the remaining copies are still landing (#7 FR5).</summary>
    Quorum,

    /// <summary>Every target copy confirmed.</summary>
    Full,
}
