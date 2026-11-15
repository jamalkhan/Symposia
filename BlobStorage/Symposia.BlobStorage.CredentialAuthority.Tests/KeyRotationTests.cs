using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;
using Symposia.BlobStorage.CredentialAuthority;
using Symposia.BlobStorage.Domain.Credentials;

namespace Symposia.BlobStorage.CredentialAuthority.Tests;

/// <summary>Master-key rotation: the keyring and online re-seal (issue #245 FR7, AC7–AC9).</summary>
public sealed class KeyRotationTests(AuthorityFixture f) : IClassFixture<AuthorityFixture>
{
    private static readonly string K1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string K2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>A fresh database in the shared container, so key counts are this test's alone.</summary>
    private async Task<string> DatabaseAsync()
    {
        var name = "keys_" + Guid.NewGuid().ToString("N")[..12];
        await using (var conn = new NpgsqlConnection(f.ConnectionString))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {name}", conn).ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(f.ConnectionString) { Database = name }.ToString();
    }

    private static async Task<AuthorityStore> StoreAsync(string db, Dictionary<string, string> keys, string active, int batch = 500, SealFormat format = SealFormat.Bound)
    {
        var store = new AuthorityStore(Options.Create(new AuthorityOptions { ConnectionString = db, MasterKeys = keys, ActiveKey = active, ResealBatchSize = batch, SealFormat = format }));
        await store.MigrateAsync(CancellationToken.None);
        return store;
    }

    private static async Task<List<CredentialGrant>> IssueAsync(AuthorityStore store, int count)
    {
        var admin = (await store.CreateTenantAsync("t", "admin@t", "operator", CancellationToken.None))!;
        var grants = new List<CredentialGrant> { admin };
        for (var i = 0; i < count; i++)
        {
            grants.Add((await store.IssueAsync("t", null, $"app{i}", AccessLevel.Read, ResourceScope.Tenant, null, [], false, admin.CredentialId, CancellationToken.None))!);
        }

        return grants;
    }

    private static ResealService Reseal(AuthorityStore store, int batch = 500) =>
        new(store, Options.Create(new AuthorityOptions { ResealBatchSize = batch, ResealPauseMilliseconds = 0 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ResealService>.Instance);

    [Fact]
    public async Task RotatingToK2_ResealsEverySecret_KeepsThemReadable_AndThenK1CanGo() // AC7
    {
        var db = await DatabaseAsync();
        var v1 = await StoreAsync(db, new() { ["k1"] = K1 }, "k1");
        var issued = await IssueAsync(v1, 7);

        var v2 = await StoreAsync(db, new() { ["k1"] = K1, ["k2"] = K2 }, "k2");
        Assert.Equal(8, (await v2.KeyCountsAsync(CancellationToken.None))["k1"]);
        await Reseal(v2, batch: 3).RunAsync(CancellationToken.None);

        var counts = await v2.KeyCountsAsync(CancellationToken.None);
        Assert.Equal(8, counts["k2"]);
        Assert.False(counts.ContainsKey("k1"));
        var onlyK2 = await StoreAsync(db, new() { ["k2"] = K2 }, "k2");
        await onlyK2.CheckKeysAsync(CancellationToken.None); // k1 removable now
        var secrets = (await onlyK2.SnapshotAsync(CancellationToken.None)).Credentials.ToDictionary(c => c.CredentialId, c => c.Secret);
        Assert.All(issued, g => Assert.Equal(g.Secret, secrets[g.CredentialId])); // still the same secrets: still authenticate
    }

    [Fact]
    public async Task RemovingAKeyStillInUse_RefusesToStart()
    {
        var db = await DatabaseAsync();
        await IssueAsync(await StoreAsync(db, new() { ["k1"] = K1 }, "k1"), 1);

        var missing = await StoreAsync(db, new() { ["k2"] = K2 }, "k2");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.CheckKeysAsync(CancellationToken.None));
        Assert.Contains("k1", ex.Message);
    }

    [Fact]
    public async Task InterruptedReseal_Resumes_WithNoSecretLostOrDoubleSealed() // AC8
    {
        var db = await DatabaseAsync();
        var issued = await IssueAsync(await StoreAsync(db, new() { ["k1"] = K1 }, "k1"), 5);
        var v2 = await StoreAsync(db, new() { ["k1"] = K1, ["k2"] = K2 }, "k2");

        Assert.Equal(2, await v2.ResealBatchAsync(2, CancellationToken.None)); // "crash" after one batch
        await Reseal(v2).RunAsync(CancellationToken.None);                       // a later run resumes

        await using var conn = new NpgsqlConnection(db);
        await conn.OpenAsync();
        await using var r = await new NpgsqlCommand("SELECT secret_sealed FROM credentials", conn).ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            Assert.StartsWith("k2:2:", r.GetString(0)); // one seal, under k2
        }

        var secrets = (await v2.SnapshotAsync(CancellationToken.None)).Credentials.ToDictionary(c => c.CredentialId, c => c.Secret);
        Assert.All(issued, g => Assert.Equal(g.Secret, secrets[g.CredentialId]));
    }

    [Fact]
    public async Task LegacyK1Ciphertexts_FromThe3Release_StillOpen_AndAreResealed()
    {
        var db = await DatabaseAsync();
        var store = await StoreAsync(db, new() { ["k1"] = K1 }, "k1");
        var issued = (await IssueAsync(store, 0))[0];
        // Rewrite the row as #3 sealed it: "k1:" + nonce|tag|cipher, no associated data.
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(issued.Secret!);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Convert.FromBase64String(K1), 16)) aes.Encrypt(nonce, plain, cipher, tag);
        await using (var conn = new NpgsqlConnection(db))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE credentials SET secret_sealed = @s, seal_key = NULL WHERE credential_id = @c", conn);
            cmd.Parameters.AddWithValue("s", "k1:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]));
            cmd.Parameters.AddWithValue("c", issued.CredentialId);
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Equal(issued.Secret, (await store.SnapshotAsync(CancellationToken.None)).Credentials.Single().Secret);
        var v2 = await StoreAsync(db, new() { ["k1"] = K1, ["k2"] = K2 }, "k2");
        await Reseal(v2).RunAsync(CancellationToken.None);
        Assert.Equal(issued.Secret, (await v2.SnapshotAsync(CancellationToken.None)).Credentials.Single().Secret);
    }

    [Fact]
    public void LegacySealFormat_WritesThe3Format_AndRefusesARotation() // Frank #2: N-1 must read what we write
    {
        var keys = new Dictionary<string, string> { ["k1"] = K1 };
        var legacy = new Keyring(keys, "k1", SealFormat.Legacy);
        var sealedSecret = legacy.Seal("secret", "cred_a");

        Assert.Matches("^k1:[A-Za-z0-9+/=]+$", sealedSecret); // no "2:" marker, no associated data
        Assert.Equal("secret", legacy.Open(sealedSecret, "cred_a"));
        Assert.Throws<InvalidOperationException>(() => new Keyring(new Dictionary<string, string> { ["k1"] = K1, ["k2"] = K2 }, "k2", SealFormat.Legacy));
        Assert.False(Reseal(new AuthorityStore(Options.Create(new AuthorityOptions { ConnectionString = "Host=unused", MasterKeys = keys, SealFormat = SealFormat.Legacy }))).Request());
    }

    [Fact]
    public async Task UnreadableRow_IsSkippedBySnapshot_AndQuarantinedByReseal_WhileTheRestMove() // Lenny #2, #3
    {
        var db = await DatabaseAsync();
        var issued = await IssueAsync(await StoreAsync(db, new() { ["k1"] = K1 }, "k1"), 3);
        await using (var conn = new NpgsqlConnection(db))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE credentials SET secret_sealed = 'k1:2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' WHERE credential_id = @c", conn);
            cmd.Parameters.AddWithValue("c", issued[1].CredentialId);
            await cmd.ExecuteNonQueryAsync();
        }

        var v2 = await StoreAsync(db, new() { ["k1"] = K1, ["k2"] = K2 }, "k2");
        var snapshot = await v2.SnapshotAsync(CancellationToken.None);
        Assert.DoesNotContain(snapshot.Credentials, c => c.CredentialId == issued[1].CredentialId);
        Assert.Equal(3, snapshot.Credentials.Count);
        Assert.True(v2.UnreadableSecrets.Values.Sum() >= 1);

        await Reseal(v2).RunAsync(CancellationToken.None);
        var counts = await v2.KeyCountsAsync(CancellationToken.None);
        Assert.Equal(3, counts["k2"]);
        Assert.Equal(1, counts["bad:k1"]);
        await v2.CheckKeysAsync(CancellationToken.None); // a quarantined row doesn't pin k1
    }

    [Fact]
    public void UnknownKeyVersion_AndASecretMovedToAnotherRow_FailLoudly() // AC9, AAD binding
    {
        var ring = new Keyring(new Dictionary<string, string> { ["k1"] = K1 }, "k1");
        var sealedForA = ring.Seal("secret-a", "cred_a");

        Assert.Equal("secret-a", ring.Open(sealedForA, "cred_a"));
        Assert.ThrowsAny<CryptographicException>(() => ring.Open(sealedForA, "cred_b"));
        var ex = Assert.Throws<CryptographicException>(() => ring.Open("k7:2:AAAA", "cred_a"));
        Assert.Contains("k7", ex.Message);
        Assert.Throws<InvalidOperationException>(() => new Keyring(new Dictionary<string, string> { ["master"] = K1 }, "master"));
    }
}
