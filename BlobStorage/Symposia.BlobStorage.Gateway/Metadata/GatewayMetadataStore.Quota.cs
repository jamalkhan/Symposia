using Microsoft.Data.Sqlite;

namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>An account's quota settings (#15). Null limits are unlimited.</summary>
public sealed record AccountQuota(string TenantId, long? PlatformLimitBytes, long? SelfLimitBytes, long? EgressLimitBytesPerMonth, bool FreeTier,
    DateTimeOffset CreatedAt);

/// <summary>A bucket's quota settings (#15). Null limits are unlimited; the account quota still applies.</summary>
public sealed record BucketQuota(string Bucket, long? LimitBytes, long? MaxObjectCount);

/// <summary>What a tenant or bucket has stored, and what in-flight writes have reserved on top (#15 FR4).</summary>
public sealed record QuotaUsage(long UsedBytes, long ReservedBytes, long Objects, long ReservedObjects);

/// <summary>Why a reservation was refused (#15 FR3): which quota, its limit, what's in use (reservations included) and what was asked.</summary>
public sealed record QuotaRefusal(string QuotaType, string Unit, long Limit, long Used, long Incoming);

/// <summary>Defaults applied the first time an account is seen (#15 FR8 free tier).</summary>
public sealed record AccountDefaults(long? PlatformLimitBytes, long? EgressLimitBytesPerMonth, bool FreeTier);

/// <summary>
/// Quota state (#15, Requirements/BlobStorage/quota-management.md), next to the usage it constrains. Bytes follow
/// <see cref="AdjustUsage"/>, which every commit, overwrite, version archive and purge already goes through, so
/// per-bucket bytes count exactly what per-tenant bytes do (current, noncurrent and soft-deleted data, each
/// once at logical size). Object counts are current objects, kept by triggers on <c>objects</c> so no write path
/// can miss them. In-flight writes hold a reservation row, checked and inserted in one IMMEDIATE transaction.
/// </summary>
public sealed partial class GatewayMetadataStore
{
    private static void InitializeQuota(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        bool fresh;
        using (var exists = Query(conn, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'bucket_usage'"))
        {
            exists.Transaction = tx;
            fresh = exists.ExecuteScalar() is null;
        }

        Run(conn, tx, """
            CREATE TABLE IF NOT EXISTS account_quota (
                tenant_id                    TEXT PRIMARY KEY,
                platform_limit_bytes         INTEGER NULL,   -- NULL = unlimited
                self_limit_bytes             INTEGER NULL,
                egress_limit_bytes_per_month INTEGER NULL,   -- provisioned only; metered elsewhere (#15 out of scope)
                free_tier                    INTEGER NOT NULL DEFAULT 0,
                created_at                   TEXT NOT NULL,
                updated_at                   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bucket_quota (
                bucket            TEXT PRIMARY KEY,
                limit_bytes       INTEGER NULL,
                max_object_count  INTEGER NULL,
                updated_at        TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bucket_usage (
                bucket   TEXT PRIMARY KEY,
                bytes    INTEGER NOT NULL DEFAULT 0,
                objects  INTEGER NOT NULL DEFAULT 0
            );

            -- One row per in-flight write between its quota check and its commit or release; a lease the write
            -- renews, so a crashed gateway's reservations lapse (#15 FR4, QA-22).
            CREATE TABLE IF NOT EXISTS quota_reservations (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id   TEXT NOT NULL,
                bucket      TEXT NOT NULL,
                bytes       INTEGER NOT NULL,
                objects     INTEGER NOT NULL,
                expires_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_quota_reservations_tenant ON quota_reservations (tenant_id, expires_at);
            CREATE INDEX IF NOT EXISTS idx_quota_reservations_bucket ON quota_reservations (bucket, expires_at);
            CREATE INDEX IF NOT EXISTS idx_quota_reservations_expiry ON quota_reservations (expires_at);

            -- Thresholds currently crossed, so each crossing alerts once (#15 FR7, AC10).
            CREATE TABLE IF NOT EXISTS quota_alert_state (
                scope          TEXT NOT NULL,   -- account | bucket
                scope_id       TEXT NOT NULL,
                threshold_pct  INTEGER NOT NULL,
                crossed_at     TEXT NOT NULL,
                PRIMARY KEY (scope, scope_id, threshold_pct)
            );

            CREATE TRIGGER IF NOT EXISTS trg_objects_count_insert AFTER INSERT ON objects BEGIN
                INSERT INTO bucket_usage (bucket, bytes, objects) VALUES (NEW.bucket, 0, 1)
                ON CONFLICT(bucket) DO UPDATE SET objects = objects + 1;
            END;
            CREATE TRIGGER IF NOT EXISTS trg_objects_count_delete AFTER DELETE ON objects BEGIN
                UPDATE bucket_usage SET objects = MAX(0, objects - 1) WHERE bucket = OLD.bucket;
            END;
            """);
        if (fresh)
        {
            // First start with quotas: count what's already stored, the same way tenant_usage does.
            Run(conn, tx, """
                INSERT INTO bucket_usage (bucket, bytes, objects)
                SELECT b.name,
                       COALESCE((SELECT SUM(o.size_bytes) FROM objects o WHERE o.bucket = b.name), 0)
                         + COALESCE((SELECT SUM(v.size_bytes) FROM object_versions v WHERE v.bucket = b.name AND v.physically_deleted_at IS NULL), 0),
                       (SELECT COUNT(*) FROM objects o WHERE o.bucket = b.name)
                FROM buckets b
                """);

            // Accounts that already exist were unlimited before quotas; they stay so (not free tier) until billing sets
            // a contracted limit. Only accounts first seen from now on start on the free tier (review: Carl #4).
            Run(conn, tx, """
                INSERT OR IGNORE INTO account_quota (tenant_id, platform_limit_bytes, free_tier, created_at, updated_at)
                SELECT tenant_id, NULL, 0, $now, $now FROM tenant_usage
                UNION SELECT DISTINCT tenant_id, NULL, 0, $now, $now FROM buckets WHERE tenant_id IS NOT NULL
                """, ("$now", Now()));
        }

        tx.Commit();
    }

    /// <summary>Moves a bucket's byte usage with its tenant's, in the caller's transaction.</summary>
    private static void AdjustBucketUsage(SqliteConnection conn, SqliteTransaction tx, string bucket, long delta) =>
        Run(conn, tx, """
            INSERT INTO bucket_usage (bucket, bytes, objects) VALUES ($b, MAX(0, $d), 0)
            ON CONFLICT(bucket) DO UPDATE SET bytes = MAX(0, bytes + $d)
            """, ("$b", bucket), ("$d", delta));

    // ── Settings ─────────────────────────────────────────────────────────────

    /// <summary>The tenant's account quota, created with <paramref name="defaults"/> the first time it's asked for (#15 FR8).</summary>
    public AccountQuota Account(string tenantId, AccountDefaults defaults)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var account = EnsureAccount(conn, tx, tenantId, defaults);
        tx.Commit();
        return account;
    }

    private static AccountQuota EnsureAccount(SqliteConnection conn, SqliteTransaction tx, string tenantId, AccountDefaults defaults)
    {
        Run(conn, tx, """
            INSERT OR IGNORE INTO account_quota (tenant_id, platform_limit_bytes, egress_limit_bytes_per_month, free_tier, created_at, updated_at)
            VALUES ($t, $p, $e, $f, $now, $now)
            """, ("$t", tenantId), ("$p", defaults.PlatformLimitBytes), ("$e", defaults.EgressLimitBytesPerMonth), ("$f", defaults.FreeTier ? 1 : 0),
            ("$now", Now()));
        using var read = Query(conn,
            "SELECT platform_limit_bytes, self_limit_bytes, egress_limit_bytes_per_month, free_tier, created_at FROM account_quota WHERE tenant_id = $t",
            ("$t", tenantId));
        read.Transaction = tx;
        using var r = read.ExecuteReader();
        r.Read();
        return new AccountQuota(tenantId, Nullable(r, 0), Nullable(r, 1), Nullable(r, 2), r.GetInt64(3) != 0, DateTimeOffset.Parse(r.GetString(4)));
    }

    /// <summary>Sets (or, with null, removes) the tenant's self-imposed limit (#15 AC7).</summary>
    public void SetSelfLimit(string tenantId, AccountDefaults defaults, long? limitBytes) =>
        UpdateAccount(tenantId, defaults, "self_limit_bytes = $v", limitBytes);

    /// <summary>
    /// Sets the platform-imposed limit (null = unlimited) and whether the account is still on the free tier; what the
    /// billing system calls when payment credentials are added (#15 FR8, AC12).
    /// </summary>
    public void SetPlatformLimit(string tenantId, AccountDefaults defaults, long? limitBytes, bool freeTier) =>
        UpdateAccount(tenantId, defaults, $"platform_limit_bytes = $v, free_tier = {(freeTier ? 1 : 0)}", limitBytes);

    private void UpdateAccount(string tenantId, AccountDefaults defaults, string assignment, long? value)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        EnsureAccount(conn, tx, tenantId, defaults);
        Run(conn, tx, $"UPDATE account_quota SET {assignment}, updated_at = $now WHERE tenant_id = $t", ("$v", value), ("$now", Now()), ("$t", tenantId));
        tx.Commit();
    }

    public BucketQuota? GetBucketQuota(string bucket)
    {
        using var conn = Open();
        using var r = Query(conn, "SELECT limit_bytes, max_object_count FROM bucket_quota WHERE bucket = $b", ("$b", bucket)).ExecuteReader();
        return r.Read() ? new BucketQuota(bucket, Nullable(r, 0), Nullable(r, 1)) : null;
    }

    /// <summary>Sets a bucket's quota; both limits null removes it (#15 AC8).</summary>
    public void SetBucketQuota(string bucket, long? limitBytes, long? maxObjectCount)
    {
        using var conn = Open();
        Execute(limitBytes is null && maxObjectCount is null
            ? Query(conn, "DELETE FROM bucket_quota WHERE bucket = $b", ("$b", bucket))
            : Query(conn, """
                INSERT INTO bucket_quota (bucket, limit_bytes, max_object_count, updated_at) VALUES ($b, $l, $m, $now)
                ON CONFLICT(bucket) DO UPDATE SET limit_bytes = excluded.limit_bytes, max_object_count = excluded.max_object_count, updated_at = excluded.updated_at
                """, ("$b", bucket), ("$l", limitBytes), ("$m", maxObjectCount), ("$now", Now())));
    }

    // ── Usage ────────────────────────────────────────────────────────────────

    public QuotaUsage AccountUsage(string tenantId)
    {
        using var conn = Open();
        return AccountUsage(conn, null, tenantId, Now());
    }

    public QuotaUsage BucketUsage(string bucket)
    {
        using var conn = Open();
        return BucketUsage(conn, null, bucket, Now());
    }

    private static QuotaUsage AccountUsage(SqliteConnection conn, SqliteTransaction? tx, string tenantId, string now)
    {
        using var cmd = Query(conn, """
            SELECT COALESCE((SELECT bytes FROM tenant_usage WHERE tenant_id = $t), 0),
                   COALESCE((SELECT SUM(bytes) FROM quota_reservations WHERE tenant_id = $t AND expires_at > $now), 0)
            """, ("$t", tenantId), ("$now", now));
        cmd.Transaction = tx;
        using var r = cmd.ExecuteReader();
        r.Read();
        return new QuotaUsage(r.GetInt64(0), r.GetInt64(1), 0, 0);
    }

    private static QuotaUsage BucketUsage(SqliteConnection conn, SqliteTransaction? tx, string bucket, string now)
    {
        using var cmd = Query(conn, """
            SELECT COALESCE((SELECT bytes FROM bucket_usage WHERE bucket = $b), 0),
                   COALESCE((SELECT SUM(bytes) FROM quota_reservations WHERE bucket = $b AND expires_at > $now), 0),
                   COALESCE((SELECT objects FROM bucket_usage WHERE bucket = $b), 0),
                   COALESCE((SELECT SUM(objects) FROM quota_reservations WHERE bucket = $b AND expires_at > $now), 0)
            """, ("$b", bucket), ("$now", now));
        cmd.Transaction = tx;
        using var r = cmd.ExecuteReader();
        r.Read();
        return new QuotaUsage(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }

    // ── Reservations (#15 FR4) ───────────────────────────────────────────────

    /// <summary>
    /// Checks a write of <paramref name="incomingBytes"/> to <paramref name="bucket"/>/<paramref name="key"/> against the
    /// account's effective limit (<paramref name="accountLimit"/>, from the caller's policy) and the bucket's quotas, and,
    /// if it fits, reserves it, all in one IMMEDIATE transaction: concurrent writers are serialised here, so two can't both
    /// take the last of the headroom (AC3). Reservations of other in-flight writes count as used. A write that lands exactly
    /// on a limit fits; once a quota is full, nothing more does, not even an empty object (spec: "writes blocked").
    /// The account is checked first, so a write over both is refused as over the account (QA-06).
    /// </summary>
    /// <returns>The reservation's ID, or the refusal.</returns>
    public (long? ReservationId, QuotaRefusal? Refusal) TryReserve(string tenantId, AccountDefaults defaults, Func<AccountQuota, long?> accountLimit,
        string bucket, string key, long incomingBytes, TimeSpan lease)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var now = Now();
        var account = EnsureAccount(conn, tx, tenantId, defaults);

        // An overwrite replaces an object rather than adding one, and in an unversioned bucket its old bytes are freed
        // by the commit, so only the growth needs room (review: Lenny #2).
        long newObject = 1;
        using (var existing = Query(conn, """
            SELECT o.size_bytes, COALESCE((SELECT versioning FROM bucket_lifecycle WHERE bucket = $b), 0)
            FROM objects o WHERE o.bucket = $b AND o.key = $k
            """, ("$b", bucket), ("$k", key)))
        {
            existing.Transaction = tx;
            using var r = existing.ExecuteReader();
            if (r.Read())
            {
                newObject = 0;
                if (r.GetInt64(1) == 0)
                {
                    incomingBytes = Math.Max(0, incomingBytes - r.GetInt64(0));
                }
            }
        }

        if (accountLimit(account) is { } limit)
        {
            var usage = AccountUsage(conn, tx, tenantId, now);
            if (Exceeds(limit, usage.UsedBytes + usage.ReservedBytes, incomingBytes))
            {
                return (null, new QuotaRefusal("account", "bytes", limit, usage.UsedBytes + usage.ReservedBytes, incomingBytes));
            }
        }


        using (var read = Query(conn, "SELECT limit_bytes, max_object_count FROM bucket_quota WHERE bucket = $b", ("$b", bucket)))
        {
            read.Transaction = tx;
            using var r = read.ExecuteReader();
            if (r.Read())
            {
                var usage = BucketUsage(conn, tx, bucket, now);
                if (Nullable(r, 0) is { } bytesLimit && Exceeds(bytesLimit, usage.UsedBytes + usage.ReservedBytes, incomingBytes))
                {
                    return (null, new QuotaRefusal("bucket", "bytes", bytesLimit, usage.UsedBytes + usage.ReservedBytes, incomingBytes));
                }

                if (Nullable(r, 1) is { } countLimit && newObject > 0 && usage.Objects + usage.ReservedObjects + newObject > countLimit)
                {
                    return (null, new QuotaRefusal("bucket", "objects", countLimit, usage.Objects + usage.ReservedObjects, newObject));
                }
            }
        }

        using var insert = Query(conn, """
            INSERT INTO quota_reservations (tenant_id, bucket, bytes, objects, expires_at) VALUES ($t, $b, $n, $o, $exp) RETURNING id
            """, ("$t", tenantId), ("$b", bucket), ("$n", Math.Max(0, incomingBytes)), ("$o", newObject),
            ("$exp", DateTimeOffset.UtcNow.Add(lease).ToString("O")));
        insert.Transaction = tx;
        var id = (long)insert.ExecuteScalar()!;
        tx.Commit();
        return (id, null);
    }

    private static bool Exceeds(long limit, long used, long incoming) => used >= limit || used + Math.Max(0, incoming) > limit;

    /// <summary>Ends a reservation, whether its write committed (usage is already charged) or failed (nothing was).</summary>
    public void EndReservation(long id)
    {
        using var conn = Open();
        Execute(Query(conn, "DELETE FROM quota_reservations WHERE id = $id", ("$id", id)));
    }

    /// <summary>Extends a live reservation's lease; false when it has already lapsed and been swept.</summary>
    public bool RenewReservation(long id, TimeSpan lease)
    {
        using var conn = Open();
        return Execute(Query(conn, "UPDATE quota_reservations SET expires_at = $exp WHERE id = $id",
            ("$exp", DateTimeOffset.UtcNow.Add(lease).ToString("O")), ("$id", id))) > 0;
    }

    /// <summary>Deletes reservations whose lease lapsed (their gateway or client vanished); returns how many (idx_quota_reservations_expiry).</summary>
    public int SweepReservations()
    {
        using var conn = Open();
        return Execute(Query(conn, "DELETE FROM quota_reservations WHERE expires_at <= $now", ("$now", Now())));
    }

    // ── Alert state (#15 FR7) ────────────────────────────────────────────────

    /// <summary>
    /// Records which of <paramref name="thresholds"/> <paramref name="utilizationPct"/> has reached, and returns those
    /// newly crossed. Thresholds it has dropped back below are re-armed, so a later crossing alerts again (QA-41).
    /// </summary>
    public IReadOnlyList<int> CrossThresholds(string scope, string scopeId, double utilizationPct, IReadOnlyList<int> thresholds)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var crossed = new List<int>();
        foreach (var threshold in thresholds)
        {
            if (utilizationPct >= threshold)
            {
                using var insert = Query(conn, "INSERT OR IGNORE INTO quota_alert_state (scope, scope_id, threshold_pct, crossed_at) VALUES ($s, $i, $p, $now)",
                    ("$s", scope), ("$i", scopeId), ("$p", threshold), ("$now", Now()));
                insert.Transaction = tx;
                if (insert.ExecuteNonQuery() > 0)
                {
                    crossed.Add(threshold);
                }
            }
            else
            {
                Run(conn, tx, "DELETE FROM quota_alert_state WHERE scope = $s AND scope_id = $i AND threshold_pct = $p",
                    ("$s", scope), ("$i", scopeId), ("$p", threshold));
            }
        }

        tx.Commit();
        return crossed;
    }

    private static long? Nullable(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
