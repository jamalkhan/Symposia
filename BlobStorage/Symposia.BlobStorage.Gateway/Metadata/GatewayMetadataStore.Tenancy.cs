using Microsoft.Data.Sqlite;

namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>
/// Bucket ownership and per-tenant stored bytes (#1 FR-2 quota; #3 tenant isolation). Usage is kept
/// incrementally inside the same transaction as every <c>objects</c> write, so a quota check is one
/// primary-key read rather than a SUM over the tenant's objects.
/// </summary>
public sealed partial class GatewayMetadataStore
{
    private static void InitializeTenancy(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var fresh = AddColumnIfMissing(conn, tx, "buckets", "tenant_id", "TEXT NULL");
        Run(conn, tx, """
            CREATE TABLE IF NOT EXISTS tenant_usage (
                tenant_id TEXT PRIMARY KEY,
                bytes     INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_buckets_tenant ON buckets (tenant_id, name);
            """);
        if (fresh)
        {
            // First start with ownership: nothing is owned yet, so there is no usage to backfill.
            Run(conn, tx, "DELETE FROM tenant_usage");
        }

        tx.Commit();
    }

    /// <summary>Adds a column in place when an older database lacks it; true when it was added.</summary>
    internal static bool AddColumnIfMissing(SqliteConnection conn, SqliteTransaction tx, string table, string column, string definition)
    {
        using (var pragma = Query(conn, $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $c", ("$c", column)))
        {
            pragma.Transaction = tx;
            if (pragma.ExecuteScalar() is not null)
            {
                return false;
            }
        }

        Run(conn, tx, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        return true;
    }

    /// <summary>Whether the index answers at all (#1 FR-24 readiness).</summary>
    public bool Ping()
    {
        try
        {
            using var conn = Open();
            return Query(conn, "SELECT 1").ExecuteScalar() is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Creates a bucket owned by <paramref name="tenantId"/>; false when the name is already taken (by anyone).</summary>
    public bool CreateBucket(string name, string tenantId)
    {
        using var conn = Open();
        return Execute(Query(conn,
            "INSERT OR IGNORE INTO buckets (name, created_at, tenant_id) VALUES ($n, $t, $tenant)",
            ("$n", name), ("$t", Now()), ("$tenant", tenantId))) > 0;
    }

    /// <summary>The bucket's owner, or null when it has none (created before ownership existed) or doesn't exist.</summary>
    public string? BucketTenant(string name)
    {
        using var conn = Open();
        return Query(conn, "SELECT tenant_id FROM buckets WHERE name = $n", ("$n", name)).ExecuteScalar() as string;
    }

    /// <summary>
    /// Assigns every unowned bucket to <paramref name="tenantId"/> and recomputes usage; used once when a
    /// single-tenant deployment upgrades to bucket ownership. Returns how many buckets were claimed.
    /// </summary>
    public int ClaimUnownedBuckets(string tenantId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        int claimed;
        using (var update = Query(conn, "UPDATE buckets SET tenant_id = $t WHERE tenant_id IS NULL", ("$t", tenantId)))
        {
            update.Transaction = tx;
            claimed = update.ExecuteNonQuery();
        }

        if (claimed > 0)
        {
            Run(conn, tx, """
                INSERT INTO tenant_usage (tenant_id, bytes)
                SELECT $t, COALESCE(SUM(o.size_bytes), 0) FROM objects o JOIN buckets b ON b.name = o.bucket WHERE b.tenant_id = $t
                ON CONFLICT(tenant_id) DO UPDATE SET bytes = excluded.bytes
                """, ("$t", tenantId));
        }

        tx.Commit();
        return claimed;
    }

    /// <summary>Bytes the tenant has stored (#1 FR-2), by primary key.</summary>
    public long TenantUsageBytes(string tenantId)
    {
        using var conn = Open();
        return Query(conn, "SELECT bytes FROM tenant_usage WHERE tenant_id = $t", ("$t", tenantId)).ExecuteScalar() as long? ?? 0;
    }

    /// <summary>Moves the owning tenant's usage by <paramref name="delta"/> bytes, in the caller's transaction.</summary>
    internal static void AdjustUsage(SqliteConnection conn, SqliteTransaction tx, string bucket, long delta)
    {
        if (delta == 0)
        {
            return;
        }

        Run(conn, tx, """
            INSERT INTO tenant_usage (tenant_id, bytes)
            SELECT tenant_id, $d FROM buckets WHERE name = $b AND tenant_id IS NOT NULL
            ON CONFLICT(tenant_id) DO UPDATE SET bytes = MAX(0, bytes + excluded.bytes)
            """, ("$b", bucket), ("$d", delta));
    }
}
