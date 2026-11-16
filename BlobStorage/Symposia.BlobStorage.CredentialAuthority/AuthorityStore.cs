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
    private readonly Keyring _keys = Keyring.From(options.Value);

    public Keyring Keys => _keys;
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

    /// <summary>The schema generation this build creates; migrations are skipped once a database has it.</summary>
    public const int SchemaGeneration = 2; // 1 = #3, 2 = #245

    /// <summary>
    /// Creates or extends the schema (expand-only). Runs under a cluster-wide advisory lock with a lock timeout, and is
    /// skipped entirely once the database is at <see cref="SchemaGeneration"/>: <c>ALTER TABLE … IF NOT EXISTS</c> takes
    /// ACCESS EXCLUSIVE before it checks, so re-running it on every replica start would queue all traffic behind any
    /// long read (#245 review: Frank #5), and two replicas starting together could race the catalog.
    /// </summary>
    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await Cmd(conn, null, "CREATE TABLE IF NOT EXISTS schema_meta (id INT PRIMARY KEY, generation INT NOT NULL)").ExecuteNonQueryAsync(ct);
        if (await Cmd(conn, null, "SELECT generation FROM schema_meta WHERE id = 1").ExecuteScalarAsync(ct) is int current && current >= SchemaGeneration)
        {
            return;
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        await Cmd(conn, tx, "SET LOCAL lock_timeout = '10s'; SELECT pg_advisory_xact_lock(hashtext('symposia.authority.migrate'))").ExecuteNonQueryAsync(ct);
        await Cmd(conn, tx, """
            CREATE TABLE IF NOT EXISTS state_version (id INT PRIMARY KEY, version BIGINT NOT NULL);
            INSERT INTO state_version (id, version) VALUES (1, 0) ON CONFLICT DO NOTHING;

            -- #245: what each version changed, so gateways can sync deltas instead of the full snapshot.
            CREATE TABLE IF NOT EXISTS change_log (
                version        BIGINT      NOT NULL,
                kind           TEXT        NOT NULL,   -- credential | tenant | rules
                entity_key     TEXT        NOT NULL,   -- credential_id | tenant_id | tenant_id \x1f bucket \x1f key
                secret_changed BOOLEAN     NOT NULL DEFAULT FALSE,
                at             TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (version, kind, entity_key)
            );
            CREATE INDEX IF NOT EXISTS idx_change_log_at ON change_log (at);
            -- The highest version pruned from change_log: a gateway behind it must take the snapshot (410).
            ALTER TABLE state_version ADD COLUMN IF NOT EXISTS log_floor BIGINT NOT NULL DEFAULT 0;
            -- The database's identity, set once: a restore or replacement shows up as a different epoch.
            ALTER TABLE state_version ADD COLUMN IF NOT EXISTS epoch TEXT NULL;
            UPDATE state_version SET epoch = md5(random()::text || clock_timestamp()::text) WHERE id = 1 AND epoch IS NULL;

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
            -- #245: which master key seals each secret (NULL = k1 from #3), so re-seal and key counts are index-backed.
            ALTER TABLE credentials ADD COLUMN IF NOT EXISTS seal_key TEXT NULL;
            CREATE INDEX IF NOT EXISTS idx_credentials_seal_key ON credentials (seal_key, credential_id);
            -- The re-seal queue's legacy (#3, NULL) arm in credential order, without re-walking re-sealed rows (Frank #6).
            CREATE INDEX IF NOT EXISTS idx_credentials_unsealed ON credentials (credential_id) WHERE seal_key IS NULL;

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
        await Cmd(conn, tx, "INSERT INTO schema_meta (id, generation) VALUES (1, @g) ON CONFLICT (id) DO UPDATE SET generation = GREATEST(schema_meta.generation, excluded.generation)",
            ("g", SchemaGeneration)).ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ── Master keys (#245 FR7) ────────────────────────────────────────────────

    /// <summary>
    /// Refuses to start when any stored secret names a key missing from the keyring (#245): a removed key then fails
    /// at deploy time, not halfway through a gateway snapshot.
    /// </summary>
    public async Task CheckKeysAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var missing = new List<string>();
        await using (var r = await Cmd(conn, null, "SELECT DISTINCT seal_key FROM credentials").ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var label = r.IsDBNull(0) ? "k1" : r.GetString(0);
                if (!label.StartsWith(Quarantined, StringComparison.Ordinal) && !_keys.Labels.Contains(label)) missing.Add(label);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Stored secrets are sealed under master key(s) {string.Join(", ", missing)}, which MasterKeys doesn't contain. " +
                "Restore the key, or re-seal before removing it.");
        }
    }

    /// <summary>How many secrets each key version seals (a scan of credentials: operator and startup only, ~20 ms at 200k rows; Frank #12).</summary>
    public async Task<IReadOnlyDictionary<string, long>> KeyCountsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var counts = new Dictionary<string, long>();
        // Grouped on the bare column, so the index can serve it; NULL (a #3-era k1 seal) is mapped here.
        await using var r = await Cmd(conn, null, "SELECT seal_key, count(*) FROM credentials GROUP BY seal_key").ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var label = r.IsDBNull(0) ? "k1" : r.GetString(0);
            counts[label] = counts.GetValueOrDefault(label) + r.GetInt64(1);
        }

        return counts;
    }

    /// <summary>
    /// Re-seals up to one batch of secrets not under the active key (#245 FR7). One short transaction per batch, rows
    /// taken FOR UPDATE SKIP LOCKED so a live rotate or revoke never waits on it; each secret is checked against its
    /// stored hash before the old ciphertext is replaced, and replaced only if unchanged meanwhile. No state version
    /// bump and no change rows: gateways hold the plaintext, which doesn't change. Returns rows taken (0 = done).
    /// </summary>
    public async Task<int> ResealBatchAsync(int batch, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = new List<(string Id, string Sealed, string Hash)>();
        // One equality lookup per key that isn't active: NULL (k1 from #3), then every other configured label. Each is
        // an index range on (seal_key, credential_id) in index order; "IS NULL OR <> @active" plans as a full scan
        // and sort. Labels outside the keyring can't exist (CheckKeysAsync refuses to start).
        foreach (var label in new string?[] { null }.Concat(_keys.Labels.Where(l => l != _keys.Active)))
        {
            if (rows.Count >= batch) break;
            await using var r = await Cmd(conn, tx, label is null
                    ? "SELECT credential_id, secret_sealed, secret_hash FROM credentials WHERE seal_key IS NULL ORDER BY credential_id LIMIT @n FOR UPDATE SKIP LOCKED"
                    : "SELECT credential_id, secret_sealed, secret_hash FROM credentials WHERE seal_key = @l ORDER BY credential_id LIMIT @n FOR UPDATE SKIP LOCKED",
                ("l", label), ("n", batch - rows.Count)).ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }

        foreach (var (id, sealedSecret, hash) in rows)
        {
            string? secret;
            try
            {
                secret = _keys.Open(sealedSecret, id);
            }
            catch (CryptographicException)
            {
                secret = null;
            }

            if (secret is null || Keyring.Hash(secret) != hash)
            {
                // Quarantined, not retried forever: one bad row must not stop the run, or the old key can never be
                // retired (#245 review: Lenny #3, Frank #7). It stays listed under /v1/keys for an operator.
                await Cmd(conn, tx, "UPDATE credentials SET seal_key = @q WHERE credential_id = @c",
                    ("q", Quarantined + Keyring.LabelOf(sealedSecret)), ("c", id)).ExecuteNonQueryAsync(ct);
                continue;
            }

            await Cmd(conn, tx, "UPDATE credentials SET secret_sealed = @new, seal_key = @active WHERE credential_id = @c AND secret_sealed = @old",
                ("new", _keys.Seal(secret, id)), ("active", _keys.Active), ("c", id), ("old", sealedSecret)).ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return rows.Count;
    }

    /// <summary>seal_key prefix of a secret re-seal couldn't open or verify; excluded from the queue and the key check.</summary>
    public const string Quarantined = "bad:";

    /// <summary>The cluster-wide re-seal lock, held on the returned connection (one re-seal at a time across replicas).</summary>
    public async Task<NpgsqlConnection?> TryLockResealAsync(CancellationToken ct)
    {
        var conn = await OpenAsync(ct);
        if (await Cmd(conn, null, "SELECT pg_try_advisory_lock(hashtext('symposia.reseal'))").ExecuteScalarAsync(ct) is true)
        {
            return conn;
        }

        await conn.DisposeAsync();
        return null;
    }

    /// <summary>One entity a mutation changed (#245 FR1).</summary>
    public sealed record Change(string Kind, string EntityKey, bool SecretChanged = false)
    {
        public static Change Credential(string id, bool secretChanged = false) => new("credential", id, secretChanged);

        public static Change Tenant(string id) => new("tenant", id);

        public static Change Rules(string tenant, string bucket, string? key) => new("rules", RulesKey(tenant, bucket, key));
    }

    private const char Sep = '\u001f';

    private static string RulesKey(string tenant, string bucket, string? key) => $"{tenant}{Sep}{bucket}{Sep}{key}";

    /// <summary>
    /// Bumps the state version and records what this mutation changed, in its own transaction, so a change and its
    /// version commit together or not at all (#245 FR1).
    /// </summary>
    /// <remarks>
    /// Load-bearing for deltas: <c>UPDATE … RETURNING</c> holds the state_version row lock until commit, so versions
    /// become visible in commit order, and a reader that sees version V sees every change at or below V. Moving the
    /// bump out of the mutation's transaction, or to a sequence, would silently break delta cursors (guarded by
    /// <c>ConcurrentMutations_ProduceGapFreeDeltas</c>).
    /// </remarks>
    private static async Task<long> BumpAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, params Change[] changes)
    {
        var version = (long)(await Cmd(conn, tx, "UPDATE state_version SET version = version + 1 WHERE id = 1 RETURNING version").ExecuteScalarAsync(ct))!;
        foreach (var change in changes.DistinctBy(c => (c.Kind, c.EntityKey)))
        {
            await Cmd(conn, tx, """
                INSERT INTO change_log (version, kind, entity_key, secret_changed) VALUES (@v, @k, @e, @s)
                ON CONFLICT (version, kind, entity_key) DO UPDATE SET secret_changed = change_log.secret_changed OR excluded.secret_changed
                """, ("v", version), ("k", change.Kind), ("e", change.EntityKey), ("s", change.SecretChanged)).ExecuteNonQueryAsync(ct);
        }

        return version;
    }

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
        await BumpAsync(conn, tx, ct, Change.Tenant(tenantId), Change.Credential(grant.CredentialId, secretChanged: true));
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
        // One tenant change, even for a purge: gateways refuse every credential of a non-active tenant already, so a
        // per-credential fan-out would only push a large tenant's purge past MaxDeltaEntities and send the whole fleet
        // to a full snapshot at once (#245 review: Lenny #10, Frank #10). The next reconcile drops the rows.
        await BumpAsync(conn, tx, ct, Change.Tenant(tenantId));

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
        await BumpAsync(conn, tx, ct, Change.Credential(grant.CredentialId, secretChanged: true));
        await tx.CommitAsync(ct);
        return grant;
    }

    private async Task<CredentialGrant> IssueInTxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string account, string identity,
        AccessLevel level, ResourceScope scope, DateTimeOffset? expiresAt, IReadOnlyList<string> regionLock, bool canManage, string? rotatedFrom,
        string actor, CancellationToken ct, string? requestedId = null)
    {
        var (accessKey, secret) = Keyring.NewKeyPair();
        var id = requestedId ?? "cred_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        // The ledger's primary key rejects any reuse, including an ID from a revoked or purged credential (AC8).
        await Cmd(conn, tx, "INSERT INTO issued_ids (credential_id, access_key_id, issued_at, tenant_id) VALUES (@c, @k, now(), @t)",
            ("c", id), ("k", accessKey), ("t", tenantId)).ExecuteNonQueryAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await Cmd(conn, tx, """
            INSERT INTO credentials (credential_id, access_key_id, secret_sealed, secret_hash, tenant_id, account_id, identity, access_level,
                scope_bucket, scope_prefix, scope_key, expires_at, region_lock, can_manage, status, created_at, rotated_from, seal_key)
            VALUES (@c, @k, @s, @h, @t, @a, @i, @l, @sb, @sp, @sk, @e, @r, @m, 'Active', @now, @rf, @key)
            """, ("c", id), ("k", accessKey), ("s", _keys.Seal(secret, id)), ("h", Keyring.Hash(secret)), ("t", tenantId), ("a", account), ("i", identity),
            ("l", level.ToString()), ("sb", scope.Bucket), ("sp", scope.Prefix), ("sk", scope.Key), ("e", expiresAt), ("r", regionLock.ToArray()),
            ("m", canManage), ("now", now), ("rf", rotatedFrom), ("key", _keys.Active)).ExecuteNonQueryAsync(ct);
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
            await BumpAsync(conn, tx, ct, Change.Credential(credentialId));
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
        await BumpAsync(conn, tx, ct, Change.Credential(credentialId), Change.Credential(grant.CredentialId, secretChanged: true));
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
        var pruned = new List<string>();
        await using (var r = await Cmd(conn, tx, "DELETE FROM credentials WHERE status = 'Revoked' AND expires_at IS NOT NULL AND expires_at < @cut RETURNING credential_id",
                         ("cut", now - TimeSpan.FromDays(options.Value.RevocationHoldingDays))).ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) pruned.Add(r.GetString(0));
        }

        if (pruned.Count > 0)
        {
            // A pruned credential has no row left: deltas report it as a tombstone.
            await BumpAsync(conn, tx, ct, [.. pruned.Select(id => Change.Credential(id))]);
        }

        await tx.CommitAsync(ct);
        return pruned.Count;
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
                    withSecret ? OpenOrNull(r.GetString(17), r.GetString(0)) : null));
            }

            return rows;
        }
    }

    /// <summary>Secrets that failed to open while serving gateways (missing key, tampered row), by key label.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> UnreadableSecrets { get; } = new();

    /// <summary>
    /// Opens one secret, or null when it can't be: one unreadable row must not fail every snapshot and delta for every
    /// gateway (#245 review: Lenny #2). A credential with no secret is never accepted by a gateway, so this fails closed
    /// for that credential alone; it is counted and logged by key label.
    /// </summary>
    private string? OpenOrNull(string sealedSecret, string credentialId)
    {
        try
        {
            return _keys.Open(sealedSecret, credentialId);
        }
        catch (CryptographicException)
        {
            UnreadableSecrets.AddOrUpdate(Keyring.LabelOf(sealedSecret), 1, (_, n) => n + 1);
            return null;
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
        await BumpAsync(conn, tx, ct, Change.Rules(tenantId, bucket, key));
        await tx.CommitAsync(ct);
    }

    // ── Snapshot (what gateways cache) ────────────────────────────────────────

    /// <summary>The whole enforceable state at one version: one repeatable-read transaction, so it's self-consistent.</summary>
    public async Task<AuthoritySnapshot> SnapshotAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        long version;
        string? epoch;
        await using (var head = await Cmd(conn, tx, "SELECT version, epoch FROM state_version WHERE id = 1").ExecuteReaderAsync(ct))
        {
            await head.ReadAsync(ct);
            (version, epoch) = (head.GetInt64(0), head.IsDBNull(1) ? null : head.GetString(1));
        }

        var tenants = new List<TenantState>();
        await using (var r = await Cmd(conn, tx, "SELECT tenant_id, status FROM tenants").ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) tenants.Add(new TenantState(r.GetString(0), Enum.Parse<TenantStatus>(r.GetString(1))));
        }

        // A credential whose secret couldn't be opened is left out (fail-closed for it alone).
        var credentials = (await ReadGrantsAsync(Cmd(conn, tx, $"SELECT {GrantColumns} FROM credentials"), true, ct)).Where(c => c.Secret is not null).ToList();
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
        return new AuthoritySnapshot(version, tenants, credentials, rules) { Epoch = epoch };
    }

    /// <summary>Why a delta can't be served: the gateway must take the full snapshot (HTTP 410).</summary>
    public enum DeltaRefusal { None, BehindRetention, AheadOfAuthority, TooLarge, IncompleteLog }

    /// <summary>
    /// The changes after <paramref name="since"/>, each entity in its current state (#245 FR2), read in one
    /// repeatable-read transaction so the delta is self-consistent. Secrets only for credentials created or
    /// re-secreted in the window (FR3).
    /// </summary>
    public async Task<(AuthorityDelta? Delta, DeltaRefusal Refusal)> ChangesAsync(long since, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        long version, floor;
        string? epoch;
        await using (var head = await Cmd(conn, tx, "SELECT version, log_floor, epoch FROM state_version WHERE id = 1").ExecuteReaderAsync(ct))
        {
            await head.ReadAsync(ct);
            (version, floor, epoch) = (head.GetInt64(0), head.GetInt64(1), head.IsDBNull(2) ? null : head.GetString(2));
        }

        if (since < floor) return (null, DeltaRefusal.BehindRetention);
        if (since > version) return (null, DeltaRefusal.AheadOfAuthority); // the database went backwards (a restore)
        // Every version holds at least one change, so the span bounds the entities: refuse before aggregating a long
        // range inside this snapshot (#245 review: Frank #9).
        if (version - since > options.Value.MaxDeltaEntities) return (null, DeltaRefusal.TooLarge);

        // Every version in the window must have its change rows. A bump without them came from an Authority binary
        // older than #245 (during a rolling deploy); serving that window as a delta would silently drop its change, a
        // revocation perhaps (#245 review: Frank #1). Index-only over the PK range, bounded by the cap above.
        var logged = (long)(await Cmd(conn, tx, "SELECT count(DISTINCT version) FROM change_log WHERE version > @since AND version <= @v",
            ("since", since), ("v", version)).ExecuteScalarAsync(ct))!;
        if (logged != version - since) return (null, DeltaRefusal.IncompleteLog);

        var changed = new List<(string Kind, string Key, bool Secret)>();
        await using (var r = await Cmd(conn, tx, """
                         SELECT kind, entity_key, bool_or(secret_changed) FROM change_log
                         WHERE version > @since AND version <= @v GROUP BY kind, entity_key
                         """, ("since", since), ("v", version)).ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                changed.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2)));
                if (changed.Count > options.Value.MaxDeltaEntities)
                {
                    return (null, DeltaRefusal.TooLarge);
                }
            }
        }

        var credentialIds = changed.Where(c => c.Kind == "credential").Select(c => c.Key).ToArray();
        var withSecret = changed.Where(c => c.Kind == "credential" && c.Secret).Select(c => c.Key).ToHashSet();
        var credentials = credentialIds.Length == 0 ? []
            : await ReadGrantsAsync(Cmd(conn, tx, $"SELECT {GrantColumns} FROM credentials WHERE credential_id = ANY(@ids)", ("ids", credentialIds)), false, ct);
        if (withSecret.Count > 0)
        {
            var opened = await ReadGrantsAsync(Cmd(conn, tx, $"SELECT {GrantColumns} FROM credentials WHERE credential_id = ANY(@ids)",
                ("ids", withSecret.ToArray())), true, ct);
            var secrets = opened.ToDictionary(g => g.CredentialId, g => g.Secret);
            credentials = [.. credentials.Select(g => secrets.TryGetValue(g.CredentialId, out var secret) ? g with { Secret = secret } : g)];
            // A new secret we can't open travels as a tombstone, as the snapshot leaves it out: a grant without its
            // secret would send every gateway to the full snapshot each time it changes (review round 2: Lenny #2).
            credentials = [.. credentials.Where(g => !withSecret.Contains(g.CredentialId) || g.Secret is not null)];
        }

        var deleted = credentialIds.Except(credentials.Select(c => c.CredentialId)).ToList();
        var tenantIds = changed.Where(c => c.Kind == "tenant").Select(c => c.Key).ToArray();
        var tenants = new List<TenantState>();
        if (tenantIds.Length > 0)
        {
            await using var r = await Cmd(conn, tx, "SELECT tenant_id, status FROM tenants WHERE tenant_id = ANY(@ids)", ("ids", tenantIds)).ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) tenants.Add(new TenantState(r.GetString(0), Enum.Parse<TenantStatus>(r.GetString(1))));
        }

        var ruleSets = new List<RuleSet>();
        foreach (var key in changed.Where(c => c.Kind == "rules").Select(c => c.Key))
        {
            var parts = key.Split(Sep, 3); // tenant and bucket can't contain the separator; an object key can (Frank #4)
            var objectKey = parts[2].Length == 0 ? null : parts[2];
            var rules = new List<PolicyRule>();
            await using var r = await Cmd(conn, tx, "SELECT effect, kind, principal FROM policy_rules WHERE tenant_id = @t AND bucket = @b AND key = @k",
                ("t", parts[0]), ("b", parts[1]), ("k", parts[2])).ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rules.Add(new PolicyRule(parts[0], parts[1], objectKey, Enum.Parse<PolicyEffect>(r.GetString(0)), Enum.Parse<PrincipalKind>(r.GetString(1)), r.GetString(2)));
            }

            ruleSets.Add(new RuleSet(parts[0], parts[1], objectKey, rules));
        }

        await tx.CommitAsync(ct);
        return (new AuthorityDelta(since, version, epoch, tenants, credentials, deleted, ruleSets), DeltaRefusal.None);
    }

    /// <summary>
    /// Drops change rows older than the retention window in batches (#245 FR1) and raises <c>log_floor</c> past them,
    /// so a gateway still behind gets 410 even when the log is empty. Returns rows deleted.
    /// </summary>
    public async Task<int> PruneChangeLogAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The newest version past retention, then fixed slices of versions up to it, each its own transaction with
        // the floor raised to exactly what was deleted (#245 review: Frank #8: bounded batches, nothing in retention lost).
        long upTo;
        await using (var conn = await OpenAsync(ct))
        {
            if (await Cmd(conn, null, "SELECT version FROM change_log WHERE at < @cut ORDER BY at DESC LIMIT 1",
                    ("cut", now - TimeSpan.FromDays(options.Value.ChangeLogRetentionDays))).ExecuteScalarAsync(ct) is not long newest)
            {
                return 0;
            }

            upTo = newest;
        }

        var total = 0;
        while (true)
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var floor = (long)(await Cmd(conn, tx, "SELECT log_floor FROM state_version WHERE id = 1 FOR UPDATE").ExecuteScalarAsync(ct))!;
            if (floor >= upTo)
            {
                return total;
            }

            var slice = Math.Min(upTo, floor + 5000);
            total += await Cmd(conn, tx, "DELETE FROM change_log WHERE version > @floor AND version <= @slice", ("floor", floor), ("slice", slice)).ExecuteNonQueryAsync(ct);
            await Cmd(conn, tx, "UPDATE state_version SET log_floor = @slice WHERE id = 1", ("slice", slice)).ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
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
