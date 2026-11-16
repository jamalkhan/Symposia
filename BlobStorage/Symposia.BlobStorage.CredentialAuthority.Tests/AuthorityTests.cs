using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Symposia.BlobStorage.CredentialAuthority;
using Symposia.BlobStorage.Domain.Credentials;
using Testcontainers.PostgreSql;

namespace Symposia.BlobStorage.CredentialAuthority.Tests;

/// <summary>A Credential Authority over a real Postgres in a container (issue #3).</summary>
public sealed class AuthorityFixture : IAsyncLifetime
{
    public const string OperatorToken = "op-token";
    public const string ServiceToken = "svc-token";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CredentialAuthority:ConnectionString"] = ConnectionString,
                ["CredentialAuthority:OperatorToken"] = OperatorToken,
                ["CredentialAuthority:ServiceToken"] = ServiceToken,
                ["CredentialAuthority:MasterKey"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()),
                ["CredentialAuthority:WatchPollMilliseconds"] = "100",
            })));
        _ = Factory.Server;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public HttpClient Operator()
    {
        var c = Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Operator-Token", OperatorToken);
        return c;
    }

    /// <summary>A gateway's client, acting for <paramref name="credentialId"/>.</summary>
    public HttpClient Service(string? credentialId = null)
    {
        var c = Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Service-Token", ServiceToken);
        if (credentialId is not null) c.DefaultRequestHeaders.Add("X-Acting-Credential", credentialId);
        return c;
    }

    /// <summary>Creates a tenant; returns its admin credential ID.</summary>
    public async Task<string> TenantAsync(string tenantId)
    {
        var response = await Operator().PostAsJsonAsync("/v1/tenants/", new { tenantId, adminIdentity = $"admin@{tenantId}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credential").GetProperty("credentialId").GetString()!;
    }
}

public sealed class AuthorityTests(AuthorityFixture f) : IClassFixture<AuthorityFixture>
{
    private static string Tenant() => "t" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task TenantProvisioning_NeedsTheOperatorToken() // FR2.4
    {
        var anonymous = await f.Factory.CreateClient().PostAsJsonAsync("/v1/tenants/", new { tenantId = Tenant(), adminIdentity = "x" });
        var service = await f.Service().PostAsJsonAsync("/v1/tenants/", new { tenantId = Tenant(), adminIdentity = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, service.StatusCode);
    }

    [Fact]
    public async Task Issue_ReturnsTheSecretOnce_AndStoresItSealed() // FR3.11
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);

        var issued = await Json(await f.Service(admin).PostAsJsonAsync("/internal/credentials", new { identity = "reader@app", accessLevel = "Read", bucket = "b" }));
        var secret = issued.GetProperty("secretAccessKey").GetString()!;
        var id = issued.GetProperty("credential").GetProperty("credentialId").GetString()!;

        var listed = await (await f.Service(admin).GetAsync("/internal/credentials")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, listed);
        await using var conn = new NpgsqlConnection(f.ConnectionString);
        await conn.OpenAsync();
        var stored = (string)(await new NpgsqlCommand($"SELECT secret_sealed FROM credentials WHERE credential_id = '{id}'", conn).ExecuteScalarAsync())!;
        Assert.DoesNotContain(secret, stored);
        Assert.Equal(secret, (await f.Factory.Services.GetRequiredService<AuthorityStore>().SnapshotAsync(CancellationToken.None))
            .Credentials.Single(c => c.CredentialId == id).Secret); // only the gateway snapshot carries it
    }

    [Fact]
    public async Task NoSelfElevation_AndOnlyManagersIssue() // FR3.10, AC11
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var scopedManager = await Json(await f.Service(admin).PostAsJsonAsync("/internal/credentials",
            new { identity = "mgr@app", accessLevel = "ReadWrite", bucket = "b", prefix = "team/", canManage = true }));
        var managerId = scopedManager.GetProperty("credential").GetProperty("credentialId").GetString()!;

        var broader = await f.Service(managerId).PostAsJsonAsync("/internal/credentials", new { identity = "x", accessLevel = "Read", bucket = "b" });
        var inside = await f.Service(managerId).PostAsJsonAsync("/internal/credentials", new { identity = "y", accessLevel = "Read", bucket = "b", prefix = "team/x/" });
        var reader = await Json(await f.Service(admin).PostAsJsonAsync("/internal/credentials", new { identity = "r", accessLevel = "Read" }));
        var byReader = await f.Service(reader.GetProperty("credential").GetProperty("credentialId").GetString()).PostAsJsonAsync("/internal/credentials",
            new { identity = "z", accessLevel = "Read" });

        Assert.Equal(HttpStatusCode.Forbidden, broader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byReader.StatusCode);
    }

    [Fact]
    public async Task CredentialIds_AreNeverReused() // FR3.12, AC8
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        await f.Service(admin).PostAsJsonAsync("/internal/credentials", new { identity = "a", accessLevel = "Read", credentialId = "cred_fixed_" + tenant });
        var revoke = await f.Service(admin).PostAsync($"/internal/credentials/cred_fixed_{tenant}/revoke", null);

        var reuse = await f.Service(admin).PostAsJsonAsync("/internal/credentials", new { identity = "b", accessLevel = "Read", credentialId = "cred_fixed_" + tenant });

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
    }

    [Fact]
    public async Task Rotation_IssuesALinkedReplacement_AndRevokesTheOld_WithAnAuditPair() // FR7, AC7
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var old = (await Json(await f.Service(admin).PostAsJsonAsync("/internal/credentials", new { identity = "svc", accessLevel = "Read", bucket = "b" })))
            .GetProperty("credential").GetProperty("credentialId").GetString()!;

        var rotated = await Json(await f.Service(admin).PostAsync($"/internal/credentials/{old}/rotate", JsonContent.Create(new { graceSeconds = 0 })));
        var store = f.Factory.Services.GetRequiredService<AuthorityStore>();
        var oldGrant = (await store.GetAsync(old, CancellationToken.None))!;
        var newId = rotated.GetProperty("credential").GetProperty("credentialId").GetString();

        Assert.Equal(CredentialStatus.Revoked, oldGrant.Status);
        Assert.Equal(newId, oldGrant.RotatedTo);
        Assert.Equal("b/*", rotated.GetProperty("credential").GetProperty("scope").GetString());
        var audit = await store.AuditAsync(tenant, null, 0, 100, CancellationToken.None);
        Assert.Contains(audit, e => e.Action == "credential.rotate.revoke" && e.CredentialId == old && e.Resource == $"rotated_to={newId}");
        Assert.Contains(audit, e => e.Action == "credential.rotate.create" && e.CredentialId == newId && e.Resource == $"rotated_from={old}");
    }

    [Fact]
    public async Task AuditChain_VerifiesIntact_AndDetectsTampering() // FR8.30
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var store = f.Factory.Services.GetRequiredService<AuthorityStore>();
        await store.RecordUsesAsync([new UseReport(admin, "GET object", "/b/k", "198.51.100.7", "us-east", "gw1", "Success", null, DateTimeOffset.UtcNow)], CancellationToken.None);

        Assert.True((await Json(await f.Service(admin).GetAsync("/internal/audit/verify"))).GetProperty("intact").GetBoolean());

        await using var conn = new NpgsqlConnection(f.ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand($"UPDATE audit_events SET outcome = 'Rejected' WHERE tenant_id = '{tenant}' AND action = 'GET object'", conn).ExecuteNonQueryAsync();
        Assert.NotNull(await store.VerifyChainAsync(tenant, CancellationToken.None));
    }

    [Fact]
    public async Task FirstExpiryRejection_IsLoggedOnce_AsTheCredentialsExpiration() // FR8.28
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var store = f.Factory.Services.GetRequiredService<AuthorityStore>();
        UseReport Expired() => new(admin, "GET object", "/b/k", null, null, "gw1", "Rejected", nameof(AccessDecision.CredentialExpired), DateTimeOffset.UtcNow);

        await store.RecordUsesAsync([Expired(), Expired()], CancellationToken.None);

        Assert.Single(await store.AuditAsync(tenant, admin, 0, 100, CancellationToken.None), e => e.Action == "credential.expire");
    }

    [Fact]
    public async Task Suspension_IsInTheSnapshot_AndRevokedCredentialsArePrunedOnlyAfterTheHoldingPeriod() // FR2.6, #16 FR6.1
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        Assert.Equal(HttpStatusCode.OK, (await f.Operator().PostAsync($"/v1/tenants/{tenant}/suspend", null)).StatusCode);
        var store = f.Factory.Services.GetRequiredService<AuthorityStore>();
        Assert.Equal(TenantStatus.Suspended, (await store.SnapshotAsync(CancellationToken.None)).Tenants.Single(t => t.TenantId == tenant).Status);
        await f.Operator().PostAsync($"/v1/tenants/{tenant}/reactivate", null);

        var temp = await store.IssueAsync(tenant, null, "temp", AccessLevel.Read, ResourceScope.Tenant, DateTimeOffset.UtcNow.AddDays(-100), [], false, admin, CancellationToken.None);
        await store.RevokeAsync(tenant, temp!.CredentialId, admin, CancellationToken.None);

        Assert.Equal(0, await store.PruneRevocationsAsync(DateTimeOffset.UtcNow.AddDays(-20), CancellationToken.None)); // 80 days past expiry
        Assert.True(await store.PruneRevocationsAsync(DateTimeOffset.UtcNow, CancellationToken.None) >= 1);              // 100 days
        Assert.Null(await store.GetAsync(temp.CredentialId, CancellationToken.None));
        Assert.Contains(await store.AuditAsync(tenant, temp.CredentialId, 0, 100, CancellationToken.None), e => e.Action == "credential.revoke"); // FR5.21
    }

    [Fact]
    public async Task RotatingARevokedCredential_IssuesNothing() // review: Frank #5
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var store = f.Factory.Services.GetRequiredService<AuthorityStore>();
        var cred = (await store.IssueAsync(tenant, null, "svc", AccessLevel.Read, ResourceScope.Tenant, null, [], false, admin, CancellationToken.None))!;
        await store.RevokeAsync(tenant, cred.CredentialId, admin, CancellationToken.None);

        Assert.Null(await store.RotateAsync(tenant, cred.CredentialId, TimeSpan.Zero, admin, CancellationToken.None));
    }

    [Fact]
    public async Task AReplayedUseBatch_IsChainedOnce() // review: Frank #7
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var store = f.Factory.Services.GetRequiredService<AuthorityStore>();
        var batch = new UseBatch("gw1", Guid.NewGuid().ToString("N"),
            [new UseReport(admin, "GET object", "/b/k", null, null, "gw1", "Success", null, DateTimeOffset.UtcNow)]);

        Assert.True(await store.RecordUsesAsync(batch, CancellationToken.None));
        Assert.False(await store.RecordUsesAsync(batch, CancellationToken.None));
        Assert.Single(await store.AuditAsync(tenant, admin, 0, 100, CancellationToken.None), e => e.Action == "GET object");
    }

    [Fact]
    public async Task UsesOfAPrunedCredential_AreStillAudited() // review: Frank #8
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var store = f.Factory.Services.GetRequiredService<AuthorityStore>();
        var old = (await store.IssueAsync(tenant, null, "old", AccessLevel.Read, ResourceScope.Tenant, DateTimeOffset.UtcNow.AddDays(-200), [], false, admin, CancellationToken.None))!;
        await store.RevokeAsync(tenant, old.CredentialId, admin, CancellationToken.None);
        await store.PruneRevocationsAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        await store.RecordUsesAsync([new UseReport(old.CredentialId, "GET object", "/b/k", "203.0.113.5", null, "gw1", "Rejected", "CredentialRevoked", DateTimeOffset.UtcNow)],
            CancellationToken.None);

        Assert.Contains(await store.AuditAsync(tenant, old.CredentialId, 0, 100, CancellationToken.None), e => e.Reason == "CredentialRevoked");
    }
}
