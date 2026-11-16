extern alias AuthorityAlias;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Symposia.BlobStorage.Gateway.Auth;
using Testcontainers.PostgreSql;
using AuthorityProgram = AuthorityAlias::Program;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Tenants and credentials end to end: Credential Authority (Postgres) → gateway cache → enforcement (issue #3).</summary>
[Collection("GatewayIntegration")]
public sealed class MultiTenancyTests : IAsyncLifetime
{
    private const string OperatorToken = "op-token";
    private const string ServiceToken = "svc-token";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private WebApplicationFactory<AuthorityProgram> _authority = null!;
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _authority = new WebApplicationFactory<AuthorityProgram>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CredentialAuthority:ConnectionString"] = _postgres.GetConnectionString(),
                ["CredentialAuthority:OperatorToken"] = OperatorToken,
                ["CredentialAuthority:ServiceToken"] = ServiceToken,
                ["CredentialAuthority:MasterKey"] = Convert.ToBase64String(new byte[32]),
                ["CredentialAuthority:WatchPollMilliseconds"] = "100",
            })));
        var authorityServer = _authority.Server;
        _cluster = await TestCluster.StartAsync(1, new Dictionary<string, string?>
        {
            ["Gateway:CredentialAuthority:Url"] = authorityServer.BaseAddress.ToString(),
            ["Gateway:CredentialAuthority:ServiceToken"] = ServiceToken,
            ["Gateway:CredentialAuthority:PollSeconds"] = "5",
        }, services: s =>
        {
            s.AddHttpClient(CredentialSync.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => authorityServer.CreateHandler());
            s.AddHttpClient(CredentialSync.HttpClientName + "-grpc").ConfigurePrimaryHttpMessageHandler(() => authorityServer.CreateHandler());
        });
    }

    public async Task DisposeAsync()
    {
        _cluster.Dispose();
        await _authority.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private async Task<(string Id, string AccessKey, string Secret)> TenantAsync(string tenant)
    {
        using var op = _authority.CreateClient();
        op.DefaultRequestHeaders.Add("X-Operator-Token", OperatorToken);
        var issued = await (await op.PostAsJsonAsync("/v1/tenants/", new { tenantId = tenant, adminIdentity = $"admin@{tenant}" })).Content.ReadFromJsonAsync<JsonElement>();
        var credentials = (Id: issued.GetProperty("credential").GetProperty("credentialId").GetString()!,
            AccessKey: issued.GetProperty("accessKeyId").GetString()!, Secret: issued.GetProperty("secretAccessKey").GetString()!);
        await KnownAsync(credentials.AccessKey);
        return credentials;
    }

    private async Task KnownAsync(string accessKey)
    {
        var cache = _cluster.Services.GetRequiredService<CredentialStore>();
        for (var i = 0; i < 200 && cache.Grant(accessKey) is null; i++) await Task.Delay(25);
        Assert.NotNull(cache.Grant(accessKey));
    }

    private AmazonS3Client S3(string accessKey, string secret) => GatewayFixture.S3ClientFor(_cluster.Gateway.Server, accessKey, secret, maxErrorRetry: 0);

    private HttpClient Signed(string accessKey, string secret) => _cluster.Signed(accessKey, secret);

    private static async Task<AmazonS3Exception> RefusedAsync(Func<Task> call) => await Assert.ThrowsAsync<AmazonS3Exception>(call);

    [Fact]
    public async Task IssuedReadOnlyPrefixCredential_Reads_IsRefusedWrites_AndRevocationPropagatesWithinTheSla()
    {
        var admin = await TenantAsync("acme");
        using var adminS3 = S3(admin.AccessKey, admin.Secret);
        await adminS3.PutBucketAsync(new PutBucketRequest { BucketName = "acme-data", UseClientRegion = true });
        await adminS3.PutObjectAsync(new PutObjectRequest { BucketName = "acme-data", Key = "reports/q3.csv", ContentBody = "q3", UseChunkEncoding = false });

        // AC1: read access to reports/ only, issued through the gateway by the tenant's own admin.
        using var adminHttp = Signed(admin.AccessKey, admin.Secret);
        var issued = await (await adminHttp.PostAsJsonAsync("/v1/credentials",
            new { identity = "dashboard@acme", accessLevel = "Read", bucket = "acme-data", prefix = "reports/" })).Content.ReadFromJsonAsync<JsonElement>();
        var readerId = issued.GetProperty("credential").GetProperty("credentialId").GetString()!;
        var reader = (Key: issued.GetProperty("accessKeyId").GetString()!, Secret: issued.GetProperty("secretAccessKey").GetString()!);
        await KnownAsync(reader.Key);
        using var readerS3 = S3(reader.Key, reader.Secret);

        using (var got = await readerS3.GetObjectAsync("acme-data", "reports/q3.csv"))
        {
            Assert.Equal(HttpStatusCode.OK, got.HttpStatusCode);
        }

        var write = await RefusedAsync(() => readerS3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = "acme-data", Key = "reports/new.csv", ContentBody = "x", UseChunkEncoding = false,
        }));
        Assert.Equal(("InsufficientCredentialScope", HttpStatusCode.Forbidden), (write.ErrorCode, write.StatusCode));
        var sibling = await RefusedAsync(() => readerS3.GetObjectAsync("acme-data", "private/x"));
        Assert.Equal("InsufficientCredentialScope", sibling.ErrorCode);

        // Review (Lenny #1, #2): a carried prefix doesn't widen a bucket-level call, and an encoded key is
        // authorized as the key the endpoint actually uses.
        using var readerHttp = Signed(reader.Key, reader.Secret);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerHttp.GetAsync("/v1/buckets/acme-data/regions?prefix=reports/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerHttp.GetAsync("/acme-data/%2572eports/q3.csv")).StatusCode);

        // AC5: revoke, then measure until this gateway refuses it.
        var revoked = Stopwatch.StartNew();
        (await adminHttp.PostAsync($"/v1/credentials/{readerId}/revoke", null)).EnsureSuccessStatusCode();
        AmazonS3Exception? refusal = null;
        while (refusal is null && revoked.Elapsed < TimeSpan.FromSeconds(60))
        {
            try
            {
                using var _ = await readerS3.GetObjectAsync("acme-data", "reports/q3.csv");
                await Task.Delay(50);
            }
            catch (AmazonS3Exception ex)
            {
                refusal = ex;
            }
        }

        Assert.Equal("CredentialRevoked", refusal?.ErrorCode);
        Assert.True(revoked.Elapsed < TimeSpan.FromSeconds(60), $"revocation took {revoked.Elapsed}");

        // AC10: uses and rejections reach the tenant's audit trail.
        var audit = "";
        for (var i = 0; i < 100 && !audit.Contains("CredentialRevoked"); i++)
        {
            await Task.Delay(100);
            var auditResponse = await adminHttp.GetAsync($"/v1/audit?credentialId={readerId}");
            audit = $"{(int)auditResponse.StatusCode} {await auditResponse.Content.ReadAsStringAsync()}";
        }

        Assert.Contains("credential.create", audit);
        Assert.Contains("credential.revoke", audit);
        Assert.Contains("\"outcome\":\"Success\"", audit);
        Assert.Contains("InsufficientCredentialScope", audit);
        Assert.Contains("CredentialRevoked", audit);
        Assert.True((await adminHttp.GetFromJsonAsync<JsonElement>("/v1/audit/verify")).GetProperty("intact").GetBoolean());

        // #245 AC5: after all those mutations, the full snapshot was pulled once (startup); everything since was deltas.
        var sync = _cluster.Services.GetRequiredService<CredentialSync>();
        Assert.Equal(1, sync.Counts.GetValueOrDefault("snapshot"));
        Assert.True(sync.Counts.GetValueOrDefault("delta") > 0);
        Assert.Equal(0, sync.Counts.GetValueOrDefault("fallback_410"));
    }

    [Fact]
    public async Task Tenants_AreIsolated_InAccessAndInListings() // FR1, AC2, AC13
    {
        var a = await TenantAsync("tenant-a");
        var b = await TenantAsync("tenant-b");
        using var aS3 = S3(a.AccessKey, a.Secret);
        using var bS3 = S3(b.AccessKey, b.Secret);
        await aS3.PutBucketAsync(new PutBucketRequest { BucketName = "a-private", UseClientRegion = true });
        await aS3.PutObjectAsync(new PutObjectRequest { BucketName = "a-private", Key = "k", ContentBody = "secret", UseChunkEncoding = false });

        var existing = await RefusedAsync(() => bS3.GetObjectAsync("a-private", "k"));
        var missing = await RefusedAsync(() => bS3.GetObjectAsync("never-created-bucket", "k"));

        Assert.Equal(("CrossTenantAccessDenied", HttpStatusCode.Forbidden), (existing.ErrorCode, existing.StatusCode));
        Assert.Equal((existing.ErrorCode, existing.StatusCode), (missing.ErrorCode, missing.StatusCode)); // indistinguishable
        using var bHttp = Signed(b.AccessKey, b.Secret);
        Assert.DoesNotContain("a-private", await bHttp.GetStringAsync("/v1/buckets"));
    }

    [Fact]
    public async Task SuspendedTenant_IsRefusedEverything_ButItsDataStays() // FR2.6, AC12
    {
        var t = await TenantAsync("suspend-me");
        using var s3 = S3(t.AccessKey, t.Secret);
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = "susp-bucket", UseClientRegion = true });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "susp-bucket", Key = "k", ContentBody = "kept", UseChunkEncoding = false });

        using var op = _authority.CreateClient();
        op.DefaultRequestHeaders.Add("X-Operator-Token", OperatorToken);
        (await op.PostAsync("/v1/tenants/suspend-me/suspend", null)).EnsureSuccessStatusCode();
        AmazonS3Exception? refusal = null;
        for (var i = 0; i < 200 && refusal is null; i++)
        {
            try { using var _ = await s3.GetObjectAsync("susp-bucket", "k"); await Task.Delay(50); }
            catch (AmazonS3Exception ex) { refusal = ex; }
        }

        Assert.Equal("TenantSuspended", refusal?.ErrorCode);
        (await op.PostAsync("/v1/tenants/suspend-me/reactivate", null)).EnsureSuccessStatusCode();
        for (var i = 0; i < 200; i++)
        {
            try
            {
                using var back = await s3.GetObjectAsync("susp-bucket", "k");
                using var reader = new StreamReader(back.ResponseStream);
                Assert.Equal("kept", await reader.ReadToEndAsync());
                return;
            }
            catch (AmazonS3Exception) { await Task.Delay(50); }
        }

        Assert.Fail("the tenant's data was not readable after reactivation");
    }
}
