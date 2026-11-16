using Npgsql;

namespace Symposia.BlobStorage.CredentialAuthority.Tests;

/// <summary>
/// The #245 hot-path queries stay index-backed (Arch §5; review: Carl #4). Shape, not exact plans: with sequential
/// scans disabled, each must name its index and never fall back to a scan of the table, which survives Postgres upgrades.
/// </summary>
public sealed class QueryPlanTests(AuthorityFixture f) : IClassFixture<AuthorityFixture>
{
    private async Task<string> PlanAsync(string sql)
    {
        _ = f.Factory.Services; // the Authority has migrated the schema
        await using var conn = new NpgsqlConnection(f.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand("SET enable_seqscan = off", conn).ExecuteNonQueryAsync();
        await using var r = await new NpgsqlCommand("EXPLAIN " + sql, conn).ExecuteReaderAsync();
        var lines = new List<string>();
        while (await r.ReadAsync()) lines.Add(r.GetString(0));
        return string.Join('\n', lines);
    }

    [Theory]
    [InlineData("SELECT kind, entity_key, bool_or(secret_changed) FROM change_log WHERE version > 10 AND version <= 200 GROUP BY kind, entity_key", "change_log_pkey")]
    [InlineData("SELECT count(DISTINCT version) FROM change_log WHERE version > 10 AND version <= 200", "change_log_pkey")]
    [InlineData("SELECT version FROM change_log WHERE at < now() ORDER BY at DESC LIMIT 1", "idx_change_log_at")]
    [InlineData("DELETE FROM change_log WHERE version > 0 AND version <= 5000", "change_log_pkey")]
    [InlineData("SELECT credential_id FROM credentials WHERE seal_key IS NULL ORDER BY credential_id LIMIT 500 FOR UPDATE SKIP LOCKED", "idx_credentials_unsealed")]
    [InlineData("SELECT credential_id FROM credentials WHERE seal_key = 'k1' ORDER BY credential_id LIMIT 500 FOR UPDATE SKIP LOCKED", "idx_credentials_seal_key")]
    public async Task HotPathQuery_UsesItsIndex(string sql, string index)
    {
        var plan = await PlanAsync(sql);

        Assert.Contains(index, plan);
        Assert.DoesNotContain("Seq Scan", plan);
    }
}
