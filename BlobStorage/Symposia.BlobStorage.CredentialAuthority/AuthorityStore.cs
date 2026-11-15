using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Symposia.BlobStorage.Domain.Credentials;

namespace Symposia.BlobStorage.CredentialAuthority;

/// <summary>An audit event (#3 FR8.29), hash-chained per tenant for tamper evidence (FR8.30).</summary>
public sealed record AuditEvent(
    long Seq, string TenantId, string? CredentialId, string Actor, string Action, string? Resource, string? SourceIp, string? Region,
    string? GatewayId, string Outcome, string? Reason, DateTimeOffset At, string PrevHash, string Hash);


/// <summary>
/// The Credential Authority's Postgres store (#3): tenants, accounts, credentials, policy rules and the audit
/// log. Every mutation bumps <c>state_version</c> in its own transaction, which is what Watch streams and
/// gateways poll; nothing a gateway caches can be ahead of or out of order with this counter.
/// </summary>
public sealed class AuthorityStore(IOptions<AuthorityOptions> options)
{
    private readonly SecretBox _box = SecretBox.FromBase64(options.Value.MasterKey);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(options.Value.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static NpgsqlCommand Cmd(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await Cmd(conn, null, """
            CREATE TABLE IF NOT EXISTS state_version (id INT PRIMARY KEY, version BIGINT NOT NULL);
            INSERT INTO state_version (id, version) VALUES (1, 0) ON CONFLICT DO NOTHING;

            CREATE TABLE IF NOT EXISTS tenants (
                tenant_id    TEXT PRIMARY KEY,
                status       TEXT NOT NULL,
                created_at   TIMESTAMPTZ NOT NULL,
                deleted_at   TIMESTAMPTZ NULL,
                purge_after  TIMESTAMPTZ NULL
            );

            CREATE TABLE IF NOT EXISTS accounts (
                account_id TEXT PRIMARY KEY,
                tenant_id  TEXT NOT NULL REFERENCES tenants (tenant_id)
            );

            -- Every credential ID ever issued: a revoked, pruned or purged one can never be issued again (FR3.12, AC8).
            CREATE TABLE IF NOT EXISTS issued_ids (credential_id TEXT PRIMARY KEY, access_key_id TEXT NOT NULL UNIQUE, issued_at TIMESTAMPTZ NOT NULL);
            -- The owning tenant outlives the credential row, so uses of a pruned credential are still audited (review: Frank #8).
            ALTER TABLE issued_ids ADD COLUMN IF NOT EXISTS tenant_id TEXT NULL;

            -- Use batches already appended, so a retried batch isn't chained twice (review: Frank #7).
            CREATE TABLE IF NOT EXISTS audit_batches (gateway_id TEXT NOT NULL, batch_id TEXT NOT NULL, received_at TIMESTAMPTZ NOT NULL,
                PRIMARY KEY (gateway_id, batch_id));

            CREATE TABLE IF NOT EXISTS credentials (
                credential_id TEXT PRIMARY KEY REFERENCES issued_ids (credential_id),
                access_key_id TEXT NOT NULL UNIQUE,
                secret_sealed TEXT NOT NULL,
                secret_hash   TEXT NOT NULL,
                tenant_id     TEXT NOT NULL REFERENCES tenants (tenant_id),
                account_id    TEXT NOT NULL REFERENCES accounts (account_id),
                identity      TEXT NOT NULL,
                access_level  TEXT NOT NULL,
                scope_bucket  TEXT NULL,
                scope_prefix  TEXT NULL,
                scope_key     TEXT NULL,
                expires_at    TIMESTAMPTZ NULL,
                region_lock   TEXT[] NOT NULL DEFAULT '{}',
                can_manage    BOOLEAN NOT NULL,
                status        TEXT NOT NULL,
                created_at    TIMESTAMPTZ NOT NULL,
                revoked_at    TIMESTAMPTZ NULL,
                revoked_by    TEXT NULL,
                rotated_from  TEXT NULL,
                rotated_to    TEXT NULL,
                expiry_logged BOOLEAN NOT NULL DEFAULT FALSE
            );
            CREATE INDEX IF NOT EXISTS idx_credentials_tenant ON credentials (tenant_id);
            CREATE INDEX IF NOT EXISTS idx_credentials_prunable ON credentials (expires_at) WHERE status = 'Revoked';

            CREATE TABLE IF NOT EXISTS policy_rules (
                tenant_id TEXT NOT NULL,
                bucket    TEXT NOT NULL,
                key       TEXT NOT NULL DEFAULT '',
                effect    TEXT NOT NULL,
                kind      TEXT NOT NULL,
                principal TEXT NOT NULL,
                PRIMARY KEY (tenant_id, bucket, key, effect, kind, principal)
            );

            CREATE TABLE IF NOT EXISTS audit_events (
                seq           BIGSERIAL PRIMARY KEY,
                tenant_id     TEXT NOT NULL,
                credential_id TEXT NULL,
                actor         TEXT NOT NULL,
                action        TEXT NOT NULL,
                resource      TEXT NULL,
                source_ip     TEXT NULL,
                region        TEXT NULL,
                gateway_id    TEXT NULL,
                outcome       TEXT NOT NULL,
                reason        TEXT NULL,
                at            TIMESTAMPTZ NOT NULL,
                prev_hash     TEXT NOT NULL,
                hash          TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_audit_tenant ON audit_events (tenant_id, seq);
            CREATE INDEX IF NOT EXISTS idx_audit_credential ON audit_events (credential_id, seq);
            """).ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> BumpAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct) =>
        (long)(await Cmd(conn, tx, "UPDATE state_version SET version = version + 1 WHERE id = 1 RETURNING version").ExecuteScalarAsync(ct))!;

    public async Task<long> VersionAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return (long)(await Cmd(conn, null, "SELECT version FROM state_version WHERE id = 1").ExecuteScalarAsync(ct))!;
    }

    // ── Tenants (FR2) ─────────────────────────────────────────────────────────

    /// <summary>Creates a tenant with a default account and its first manager credential; the secret is in the result, once.</summary>
    public async Task<CredentialGrant?> CreateTenantAsync(string tenantId, string identity, string actor, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var inserted = await Cmd(conn, tx, "INSERT INTO tenants (tenant_id, status, created_at) VALUES (@t, 'Active', now()) ON CONFLICT DO NOTHING",
            ("t", tenantId)).ExecuteNonQueryAsync(ct);
        if (inserted == 0)
        {
            return null;
        }

        var account = $"{tenantId}-default";
        await Cmd(conn, tx, "INSERT INTO accounts (account_id, tenant_id) VALUES (@a, @t)", ("a", account), ("t", tenantId)).ExecuteNonQueryAsync(ct);
        var grant = await IssueInTxAsync(conn, tx, tenantId, account, identity, AccessLevel.ReadWrite, ResourceScope.Tenant, null, [], true, null, actor, ct);
        await AuditInTxAsync(conn, tx, tenantId, null, actor, "tenant.create", null, null, null, null, "Success", null, ct);
        await BumpAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
        return grant;
    }

    public async Task<bool> SetTenantStatusAsync(string tenantId, TenantStatus status, string actor, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = await Cmd(conn, tx, """
            UPDATE tenants SET status = @s,
                deleted_at = CASE WHEN @s = 'SoftDeleted' THEN now() WHEN @s = 'Active' THEN NULL ELSE deleted_at END,
                purge_after = CASE WHEN @s = 'SoftDeleted' THEN now() + make_interval(days => @d) WHEN @s = 'Active' THEN NULL ELSE purge_after END
            WHERE tenant_id = @t AND status <> 'Purged' AND (@s <> 'Purged' OR (status = 'SoftDeleted' AND purge_after <= now()))
            """, ("s", status.ToString()), ("t", tenantId), ("d", options.Value.TenantRecoveryDays)).ExecuteNonQueryAsync(ct);
        if (changed == 0)
        {
            return false;
        }

        if (status == TenantStatus.Purged)
        {
            // Hard delete (FR2.7): every credential is revoked for good; IDs stay in issued_ids and the audit trail stays.
            await Cmd(conn, tx, "UPDATE credentials SET status = 'Revoked', revoked_at = COALESCE(revoked_at, now()), revoked_by = @a WHERE tenant_id = @t",
                ("a", actor), ("t", tenantId)).ExecuteNonQueryAsync(ct);
        }

        await AuditInTxAsync(conn, tx, tenantId, null, actor, $"tenant.{status.ToString().ToLowerInvariant()}", null, null, null, null, "Success", null, ct);
        await BumpAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ── Credentials (FR3, FR5, FR7) ───────────────────────────────────────────

    public async Task<CredentialGrant?> GetAsync(string credentialId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return (await ReadGrantsAsync(Cmd(conn, null, $"SELECT {GrantColumns} FROM credentials WHERE credential_id = @c", ("c", credentialId)), false, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<CredentialGrant>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return await ReadGrantsAsync(Cmd(conn, null, $"SELECT {GrantColumns} FROM credentials WHERE tenant_id = @t ORDER BY created_at", ("t", tenantId)), false, ct);
    }

    /// <summary>Issues a credential under <paramref name="tenantId"/>; null when the account isn't the tenant's.</summary>
    public async Task<CredentialGrant?> IssueAsync(string tenantId, string? accountId, string identity, AccessLevel level, ResourceScope scope,
        DateTimeOffset? expiresAt, IReadOnlyList<string> regionLock, bool canManage, string actor, CancellationToken ct, string? requestedId = null)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var account = accountId ?? $"{tenantId}-default";
        if (await Cmd(conn, tx, "SELECT 1 FROM accounts WHERE account_id = @a AND tenant_id = @t", ("a", account), ("t", tenantId)).ExecuteScalarAsync(ct) is null)
        {
            return null;
        }

        var grant = await IssueInTxAsync(conn, tx, tenantId, account, identity, level, scope, expiresAt, regionLock, canManage, null, actor, ct, requestedId);
        await AuditInTxAsync(conn, tx, tenantId, grant.CredentialId, actor, "credential.create", scope.ToString(), null, null, null, "Success", null, ct);
        await BumpAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
        return grant;
    }

    private async Task<CredentialGrant> IssueInTxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string account, string identity,
        AccessLevel level, ResourceScope scope, DateTimeOffset? expiresAt, IReadOnlyList<string> regionLock, bool canManage, string? rotatedFrom,
        string actor, CancellationToken ct, string? requestedId = null)
    {
        var (accessKey, secret) = SecretBox.NewKeyPair();
        var id = requestedId ?? "cred_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        // The ledger's primary key rejects any reuse, including an ID from a revoked or purged credential (AC8).
        await Cmd(conn, tx, "INSERT INTO issued_ids (credential_id, access_key_id, issued_at, tenant_id) VALUES (@c, @k, now(), @t)",
            ("c", id), ("k", accessKey), ("t", tenantId)).ExecuteNonQueryAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await Cmd(conn, tx, """
            INSERT INTO credentials (credential_id, access_key_id, secret_sealed, secret_hash, tenant_id, account_id, identity, access_level,
                scope_bucket, scope_prefix, scope_key, expires_at, region_lock, can_manage, status, created_at, rotated_from)
            VALUES (@c, @k, @s, @h, @t, @a, @i, @l, @sb, @sp, @sk, @e, @r, @m, 'Active', @now, @rf)
            """, ("c", id), ("k", accessKey), ("s", _box.Seal(secret)), ("h", SecretBox.Hash(secret)), ("t", tenantId), ("a", account), ("i", identity),
            ("l", level.ToString()), ("sb", scope.Bucket), ("sp", scope.Prefix), ("sk", scope.Key), ("e", expiresAt), ("r", regionLock.ToArray()),
            ("m", canManage), ("now", now), ("rf", rotatedFrom)).ExecuteNonQueryAsync(ct);
        return new CredentialGrant(id, accessKey, tenantId, account, identity, level, scope, expiresAt, regionLock, canManage, CredentialStatus.Active, now,
            RotatedFrom: rotatedFrom, Secret: secret);
    }

    /// <summary>Revokes a credential (FR5); with a future <paramref name="effectiveAt"/> it keeps working until then (rotation overlap).</summary>
    public async Task<bool> RevokeAsync(string tenantId, string credentialId, string actor, CancellationToken ct, DateTimeOffset? effectiveAt = null)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = await RevokeInTxAsync(conn, tx, tenantId, credentialId, actor, effectiveAt, ct);
        if (changed)
        {
            await AuditInTxAsync(conn, tx, tenantId, credentialId, actor, "credential.revoke", null, null, null, null, "Success", null, ct);
            await BumpAsync(conn, tx, ct);
        }

        await tx.CommitAsync(ct);
        return changed;
    }

    private static async Task<bool> RevokeInTxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string credentialId, string actor,
        DateTimeOffset? effectiveAt, CancellationToken ct) =>
        await Cmd(conn, tx, """
            UPDATE credentials SET status = CASE WHEN @at::timestamptz IS NULL OR @at::timestamptz <= now() THEN 'Revoked' ELSE status END,
                revoked_at = COALESCE(@at::timestamptz, now()), revoked_by = @a
            WHERE credential_id = @c AND tenant_id = @t AND status = 'Active'
            """, ("at", effectiveAt), ("a", actor), ("c", credentialId), ("t", tenantId)).ExecuteNonQueryAsync(ct) > 0;

    /// <summary>
    /// Rotation (FR7): a new credential with the old one's configuration (or <paramref name="overrides"/>), and the
    /// old one revoked now or after <paramref name="grace"/>; logged as a linked pair (FR7.27).
    /// </summary>
    public async Task<CredentialGrant?> RotateAsync(string tenantId, string credentialId, TimeSpan grace, string actor, CancellationToken ct,
        Func<CredentialGrant, CredentialGrant>? overrides = null)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Read and lock the old credential inside the transaction: a revoke racing this rotate either lands
        // first (and rotate refuses) or waits; it can never be undone by a fresh copy (review: Frank #5).
        var old = (await ReadGrantsAsync(Cmd(conn, tx, $"SELECT {GrantColumns} FROM credentials WHERE credential_id = @c FOR UPDATE",
            ("c", credentialId)), false, ct)).FirstOrDefault();
        if (old is null || old.TenantId != tenantId || old.Status != CredentialStatus.Active || old.RevokedAt is not null)
        {
            return null;
        }

        var shape = overrides?.Invoke(old) ?? old;
        var grant = await IssueInTxAsync(conn, tx, tenantId, shape.AccountId, shape.Identity, shape.AccessLevel, shape.Scope, shape.ExpiresAt,
            shape.RegionLock, shape.CanManage, credentialId, actor, ct);
        if (!await RevokeInTxAsync(conn, tx, tenantId, credentialId, actor, grace > TimeSpan.Zero ? DateTimeOffset.UtcNow + grace : null, ct))
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        await Cmd(conn, tx, "UPDATE credentials SET rotated_to = @n WHERE credential_id = @c", ("n", grant.CredentialId), ("c", credentialId)).ExecuteNonQueryAsync(ct);
        await AuditInTxAsync(conn, tx, tenantId, credentialId, actor, "credential.rotate.revoke", $"rotated_to={grant.CredentialId}", null, null, null, "Success", null, ct);
        await AuditInTxAsync(conn, tx, tenantId, grant.CredentialId, actor, "credential.rotate.create", $"rotated_from={credentialId}", null, null, null, "Success", null, ct);
        await BumpAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
        return grant with { RotatedFrom = credentialId };
    }

    /// <summary>
    /// Revocations kept past <see cref="AuthorityOptions.RevocationHoldingDays"/> after expiry are pruned from the
    /// active set (#16 FR6.1); the ID stays in issued_ids and every event in the audit log (#3 FR5.21).
    /// </summary>
    public async Task<int> PruneRevocationsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // A grace-period revocation is Revoked once its instant passes, though no one rewrote its status (review: Frank #8).
        await Cmd(conn, tx, "UPDATE credentials SET status = 'Revoked' WHERE status = 'Active' AND revoked_at IS NOT NULL AND revoked_at <= @now",
            ("now", now)).ExecuteNonQueryAsync(ct);
        var pruned = await Cmd(conn, tx, "DELETE FROM credentials WHERE status = 'Revoked' AND expires_at IS NOT NULL AND expires_at < @cut",
            ("cut", now - TimeSpan.FromDays(options.Value.RevocationHoldingDays))).ExecuteNonQueryAsync(ct);
        if (pruned > 0)
        {
            await BumpAsync(conn, tx, ct);
        }

        await tx.CommitAsync(ct);
        return pruned;
    }

    private const string GrantColumns =
        "credential_id, access_key_id, tenant_id, account_id, identity, access_level, scope_bucket, scope_prefix, scope_key, expires_at, " +
        "region_lock, can_manage, status, created_at, revoked_at, rotated_from, rotated_to, secret_sealed";

    private async Task<IReadOnlyList<CredentialGrant>> ReadGrantsAsync(NpgsqlCommand cmd, bool withSecret, CancellationToken ct)
    {
        await using (cmd)
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<CredentialGrant>();
            while (await r.ReadAsync(ct))
            {
                DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : new DateTimeOffset(r.GetDateTime(i), TimeSpan.Zero);
                string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                rows.Add(new CredentialGrant(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    Enum.Parse<AccessLevel>(r.GetString(5)), new ResourceScope(Str(6), Str(7), Str(8)), At(9), (string[])r.GetValue(10),
                    r.GetBoolean(11), Enum.Parse<CredentialStatus>(r.GetString(12)), At(13)!.Value, At(14), Str(15), Str(16),
                    withSecret ? _box.Open(r.GetString(17)) : null));
            }

            return rows;
        }
    }

    // ── Policies (FR6) ────────────────────────────────────────────────────────

    public async Task SetRulesAsync(string tenantId, string bucket, string? key, IReadOnlyList<PolicyRule> rules, string actor, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Cmd(conn, tx, "DELETE FROM policy_rules WHERE tenant_id = @t AND bucket = @b AND key = @k", ("t", tenantId), ("b", bucket), ("k", key ?? "")).ExecuteNonQueryAsync(ct);
        foreach (var rule in rules)
        {
            await Cmd(conn, tx, "INSERT INTO policy_rules (tenant_id, bucket, key, effect, kind, principal) VALUES (@t, @b, @k, @e, @n, @p) ON CONFLICT DO NOTHING",
                ("t", tenantId), ("b", bucket), ("k", key ?? ""), ("e", rule.Effect.ToString()), ("n", rule.Kind.ToString()), ("p", rule.Principal)).ExecuteNonQueryAsync(ct);
        }

        await AuditInTxAsync(conn, tx, tenantId, null, actor, key is null ? "policy.bucket.set" : "policy.object.set", key is null ? bucket : $"{bucket}/{key}",
            null, null, null, "Success", null, ct);
        await BumpAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
    }

    // ── Snapshot (what gateways cache) ────────────────────────────────────────

    /// <summary>The whole enforceable state at one version: one repeatable-read transaction, so it's self-consistent.</summary>
    public async Task<AuthoritySnapshot> SnapshotAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var version = (long)(await Cmd(conn, tx, "SELECT version FROM state_version WHERE id = 1").ExecuteScalarAsync(ct))!;
        var tenants = new List<TenantState>();
        await using (var r = await Cmd(conn, tx, "SELECT tenant_id, status FROM tenants").ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) tenants.Add(new TenantState(r.GetString(0), Enum.Parse<TenantStatus>(r.GetString(1))));
        }

        var credentials = await ReadGrantsAsync(Cmd(conn, tx, $"SELECT {GrantColumns} FROM credentials"), true, ct);
        var rules = new List<PolicyRule>();
        await using (var r = await Cmd(conn, tx, "SELECT tenant_id, bucket, key, effect, kind, principal FROM policy_rules").ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                rules.Add(new PolicyRule(r.GetString(0), r.GetString(1), r.GetString(2) is { Length: > 0 } k ? k : null,
                    Enum.Parse<PolicyEffect>(r.GetString(3)), Enum.Parse<PrincipalKind>(r.GetString(4)), r.GetString(5)));
            }
        }

        await tx.CommitAsync(ct);
        return new AuthoritySnapshot(version, tenants, credentials, rules);
    }

    // ── Audit (FR8) ───────────────────────────────────────────────────────────

    /// <summary>Appends gateway-reported uses; the first rejection for natural expiry is also logged as the credential's expiration (FR8.28).</summary>
    public Task RecordUsesAsync(IReadOnlyList<UseReport> uses, CancellationToken ct) =>
        RecordUsesAsync(new UseBatch("", Guid.NewGuid().ToString("N"), uses), ct);

    /// <summary>Appends a batch once: false when this gateway already delivered it (a retry after a lost acknowledgement).</summary>
    public async Task<bool> RecordUsesAsync(UseBatch batch, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await Cmd(conn, tx, "INSERT INTO audit_batches (gateway_id, batch_id, received_at) VALUES (@g, @b, now()) ON CONFLICT DO NOTHING",
                ("g", batch.GatewayId), ("b", batch.BatchId)).ExecuteNonQueryAsync(ct) == 0)
        {
            return false;
        }

        // Tenants in one fixed order, so two gateways' batches never take the per-tenant chain locks in
        // opposite orders and deadlock (review: Frank #6).
        var tenants = new Dictionary<string, string?>();
        foreach (var id in batch.Uses.Select(u => u.CredentialId).Distinct())
        {
            tenants[id] = await Cmd(conn, tx, "SELECT tenant_id FROM issued_ids WHERE credential_id = @c", ("c", id)).ExecuteScalarAsync(ct) as string;
        }

        foreach (var use in batch.Uses.OrderBy(u => tenants[u.CredentialId], StringComparer.Ordinal).ThenBy(u => u.At))
        {
            var tenant = tenants[use.CredentialId];
            if (tenant is null)
            {
                continue; // a static config credential, unknown to the Authority
            }

            await AuditInTxAsync(conn, tx, tenant, use.CredentialId, use.CredentialId, use.Action, use.Resource, use.SourceIp, use.Region, use.GatewayId,
                use.Outcome, use.Reason, ct, use.At);
            if (use.Reason == nameof(AccessDecision.CredentialExpired) && await Cmd(conn, tx,
                    "UPDATE credentials SET expiry_logged = TRUE WHERE credential_id = @c AND NOT expiry_logged", ("c", use.CredentialId)).ExecuteNonQueryAsync(ct) > 0)
            {
                await AuditInTxAsync(conn, tx, tenant, use.CredentialId, "system", "credential.expire", null, null, null, use.GatewayId, "Success", null, ct, use.At);
            }
        }

        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task AuditInTxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string? credentialId, string actor, string action,
        string? resource, string? sourceIp, string? region, string? gatewayId, string outcome, string? reason, CancellationToken ct, DateTimeOffset? at = null)
    {
        // Serialize appends per tenant so the chain has one tail (an advisory lock scoped to this transaction).
        await Cmd(conn, tx, "SELECT pg_advisory_xact_lock(hashtext(@t))", ("t", tenantId)).ExecuteNonQueryAsync(ct);
        var prev = await Cmd(conn, tx, "SELECT hash FROM audit_events WHERE tenant_id = @t ORDER BY seq DESC LIMIT 1", ("t", tenantId)).ExecuteScalarAsync(ct) as string
            ?? new string('0', 64);
        var when = (at ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var hash = ChainHash(prev, tenantId, credentialId, actor, action, resource, sourceIp, region, gatewayId, outcome, reason, when);
        await Cmd(conn, tx, """
            INSERT INTO audit_events (tenant_id, credential_id, actor, action, resource, source_ip, region, gateway_id, outcome, reason, at, prev_hash, hash)
            VALUES (@t, @c, @a, @act, @res, @ip, @reg, @g, @o, @r, @at, @p, @h)
            """, ("t", tenantId), ("c", credentialId), ("a", actor), ("act", action), ("res", resource), ("ip", sourceIp), ("reg", region), ("g", gatewayId),
            ("o", outcome), ("r", reason), ("at", when), ("p", prev), ("h", hash)).ExecuteNonQueryAsync(ct);
    }

    /// <summary>SHA-256 over the previous hash and the event's canonical JSON: changing or removing any event breaks every later hash.</summary>
    public static string ChainHash(string prev, string tenantId, string? credentialId, string actor, string action, string? resource, string? sourceIp,
        string? region, string? gatewayId, string outcome, string? reason, DateTimeOffset at)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            tenantId, credentialId, actor, action, resource, sourceIp, region, gatewayId, outcome, reason,
            at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
        }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prev + canonical)));
    }

    public async Task<IReadOnlyList<AuditEvent>> AuditAsync(string tenantId, string? credentialId, long after, int limit, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var r = await Cmd(conn, null, """
            SELECT seq, tenant_id, credential_id, actor, action, resource, source_ip, region, gateway_id, outcome, reason, at, prev_hash, hash
            FROM audit_events WHERE tenant_id = @t AND (@c::text IS NULL OR credential_id = @c) AND seq > @after ORDER BY seq LIMIT @l
            """, ("t", tenantId), ("c", credentialId), ("after", after), ("l", limit)).ExecuteReaderAsync(ct);
        var rows = new List<AuditEvent>();
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            rows.Add(new AuditEvent(r.GetInt64(0), r.GetString(1), S(2), r.GetString(3), r.GetString(4), S(5), S(6), S(7), S(8), r.GetString(9), S(10),
                new DateTimeOffset(r.GetDateTime(11), TimeSpan.Zero), r.GetString(12), r.GetString(13)));
        }

        return rows;
    }

    /// <summary>Recomputes the tenant's whole chain; the first event that doesn't verify, or null when it's intact.</summary>
    public async Task<long?> VerifyChainAsync(string tenantId, CancellationToken ct)
    {
        var prev = new string('0', 64);
        long after = 0;
        while (true)
        {
            var page = await AuditAsync(tenantId, null, after, 1000, ct);
            foreach (var e in page)
            {
                if (e.PrevHash != prev || e.Hash != ChainHash(prev, e.TenantId, e.CredentialId, e.Actor, e.Action, e.Resource, e.SourceIp, e.Region,
                        e.GatewayId, e.Outcome, e.Reason, e.At))
                {
                    return e.Seq;
                }

                prev = e.Hash;
            }

            if (page.Count < 1000) return null;
            after = page[^1].Seq;
        }
    }
}
