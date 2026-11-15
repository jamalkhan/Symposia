using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// The hot queries added by #1, #7, #8 and #16 stay index-backed (no full scans of objects, gc_queue or
/// object_versions). Set SYMPOSIA_QUERY_PLAN_OUT to a file path to also write every plan out for review.
/// </summary>
public sealed class QueryPlanTests
{
    private static readonly (string Name, string Sql, string MustUse)[] Queries =
    [
        ("#1 tenant usage", "SELECT bytes FROM tenant_usage WHERE tenant_id = 't'", "tenant_usage"),
        ("#3 tenant's buckets", "SELECT name, created_at FROM buckets WHERE tenant_id = 't' ORDER BY name", "idx_buckets_tenant"),
        ("#7 short of target (first page)",
            "SELECT bucket, key FROM objects WHERE replica_count >= 1 AND target_copies > replica_count ORDER BY (target_copies - replica_count), bucket, key LIMIT 500",
            "idx_objects_short"),
        ("#7 short of target (cursor)",
            "SELECT bucket, key FROM objects WHERE replica_count >= 1 AND target_copies > replica_count AND ((target_copies - replica_count), bucket, key) > (1, 'b', 'k') ORDER BY (target_copies - replica_count), bucket, key LIMIT 500",
            "idx_objects_short"),
        ("#8 references (dedup/GC)",
            "SELECT 1 FROM objects WHERE cid = 'c' AND scheme = 'replica' UNION ALL SELECT 1 FROM multipart_parts WHERE cid = 'c' UNION ALL SELECT 1 FROM object_shards WHERE shard_cid = 'c' UNION ALL SELECT 1 FROM object_versions WHERE cid = 'c' AND physically_deleted_at IS NULL AND purge_queued_at IS NULL LIMIT 1",
            "idx_object_shards_cid"),
        ("#8 shards of an object", "SELECT shard_index FROM object_shards WHERE bucket = 'b' AND key = 'k' AND object_cid = 'o' ORDER BY shard_index", "sqlite_autoindex_object_shards_1"),
        ("#8 erasure-coded scan", "SELECT bucket, key FROM objects WHERE scheme <> 'replica' AND (bucket, key) > ('', '') ORDER BY bucket, key LIMIT 500", "idx_objects_erasure"),
        ("#8 open repair tasks", "SELECT id FROM repair_tasks WHERE completed_at IS NULL ORDER BY priority, triggered_at LIMIT 100", "idx_repair_tasks_open"),
        ("#8 random object", "SELECT bucket FROM objects WHERE rowid >= 5 AND scheme = 'replica' AND replica_count > 0 ORDER BY rowid LIMIT 1", "INTEGER PRIMARY KEY"),
        ("#16 GC queue (tier)", "SELECT id FROM gc_queue WHERE queued_at <= 'x' AND priority < 2 ORDER BY priority, attempts, id LIMIT 50", "idx_gc_queue_due"),
        ("#16 purgeable versions",
            "SELECT id FROM object_versions WHERE purge_queued_at IS NULL AND deleted_at IS NOT NULL AND deleted_at < 'x' AND legal_hold = 0 ORDER BY deleted_at LIMIT 500",
            "idx_versions_purgeable"),
        ("#16 versions of a key", "SELECT id FROM object_versions WHERE bucket = 'b' AND key = 'k' ORDER BY created_at, id", "idx_versions_key"),
        ("#16 versions waiting on a blob",
            "SELECT v.id FROM version_purges p JOIN object_versions v ON v.id = p.version_id WHERE p.cid = 'c' AND v.physically_deleted_at IS NULL",
            "idx_version_purges_cid"),
    ];

    [Fact]
    public void NewQueries_AreIndexBacked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"symposia-plans-{Guid.NewGuid():N}", "m.db");
        new GatewayMetadataStore(Options.Create(new GatewayOptions { MetadataDbPath = path })).Initialize();
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        var report = new System.Text.StringBuilder();
        foreach (var (name, sql, mustUse) in Queries)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var r = cmd.ExecuteReader();
            var plan = new List<string>();
            while (r.Read()) plan.Add(r.GetString(3));
            report.AppendLine($"-- {name}\n{sql}\n  " + string.Join("\n  ", plan) + "\n");
            Assert.True(plan.Any(p => p.Contains(mustUse, StringComparison.Ordinal)), $"{name}: expected {mustUse} in plan: {string.Join(" | ", plan)}");
            Assert.DoesNotContain(plan, p => p.StartsWith("SCAN objects", StringComparison.Ordinal) && !p.Contains("USING", StringComparison.Ordinal));
        }

        if (Environment.GetEnvironmentVariable("SYMPOSIA_QUERY_PLAN_OUT") is { Length: > 0 } outFile)
        {
            File.WriteAllText(outFile, report.ToString());
        }
    }
}
