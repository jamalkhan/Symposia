using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.CredentialAuthority;
using Symposia.BlobStorage.Domain.Credentials;

namespace Symposia.BlobStorage.CredentialAuthority.Tests;

/// <summary>Incremental sync: the change log and the delta endpoint (issue #245 FR1–FR3).</summary>
public sealed class DeltaTests(AuthorityFixture f) : IClassFixture<AuthorityFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private AuthorityStore Store => f.Factory.Services.GetRequiredService<AuthorityStore>();

    private static string Tenant() => "d" + Guid.NewGuid().ToString("N")[..10];

    private async Task<AuthorityDelta> DeltaAsync(long since)
    {
        var (delta, refusal) = await Store.ChangesAsync(since, CancellationToken.None);
        Assert.Equal(AuthorityStore.DeltaRefusal.None, refusal);
        return delta!;
    }

    [Fact]
    public async Task Revocation_DeltaCarriesTheCredential_AndNoSecretAtAll() // AC1
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var cred = (await Store.IssueAsync(tenant, null, "svc", AccessLevel.Read, ResourceScope.Tenant, null, [], false, admin, CancellationToken.None))!;
        var before = await Store.VersionAsync(CancellationToken.None);

        await Store.RevokeAsync(tenant, cred.CredentialId, admin, CancellationToken.None);
        var delta = await DeltaAsync(before);

        var revoked = Assert.Single(delta.Credentials);
        Assert.Equal(cred.CredentialId, revoked.CredentialId);
        Assert.Equal(CredentialStatus.Revoked, revoked.Status);
        Assert.All(delta.Credentials, c => Assert.Null(c.Secret));
    }

    [Fact]
    public async Task Issuance_DeltaCarriesOnlyTheNewSecret() // AC2, FR3
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var before = await Store.VersionAsync(CancellationToken.None);
        var issued = (await Store.IssueAsync(tenant, null, "new", AccessLevel.Read, ResourceScope.Tenant, null, [], false, admin, CancellationToken.None))!;
        var other = (await Store.IssueAsync(tenant, null, "other", AccessLevel.Read, ResourceScope.Tenant, null, [], false, admin, CancellationToken.None))!;
        var mid = await Store.VersionAsync(CancellationToken.None);
        await Store.RevokeAsync(tenant, issued.CredentialId, admin, CancellationToken.None);

        // Across the whole window both were created: both secrets travel (still usable or just issued).
        Assert.Equal(2, (await DeltaAsync(before)).Credentials.Count(c => c.Secret is not null));
        // After their creation only the revocation changed: no secret.
        var later = await DeltaAsync(mid);
        Assert.Equal(issued.CredentialId, Assert.Single(later.Credentials).CredentialId);
        Assert.Null(later.Credentials[0].Secret);
        _ = other;
    }

    [Fact]
    public async Task OutOfRangeCursors_AndOversizedDeltas_Return410() // AC3, Q2, Q6
    {
        var version = await Store.VersionAsync(CancellationToken.None);
        using var http = f.Service();

        Assert.Equal(HttpStatusCode.Gone, (await http.GetAsync($"/internal/changes?since={version + 1000}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/internal/changes?since=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/internal/changes?since={version}")).StatusCode);

        var tight = new AuthorityStore(Options.Create(new AuthorityOptions
        {
            ConnectionString = f.ConnectionString, MasterKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()), MaxDeltaEntities = 1,
        }));
        var tenant = Tenant();
        await f.TenantAsync(tenant); // a tenant and its admin credential: two entities
        Assert.Equal(AuthorityStore.DeltaRefusal.TooLarge, (await tight.ChangesAsync(version, CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task PrunedChangeLog_RaisesTheFloor_SoLaggingGatewaysGet410() // FR1 retention
    {
        var start = await Store.VersionAsync(CancellationToken.None);
        await f.TenantAsync(Tenant());

        var pruned = await Store.PruneChangeLogAsync(DateTimeOffset.UtcNow.AddDays(30), CancellationToken.None);

        Assert.True(pruned > 0);
        Assert.Equal(AuthorityStore.DeltaRefusal.BehindRetention, (await Store.ChangesAsync(start, CancellationToken.None)).Refusal);
        Assert.Equal(AuthorityStore.DeltaRefusal.None, (await Store.ChangesAsync(await Store.VersionAsync(CancellationToken.None), CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task PrunedCredential_IsATombstone_AndRuleSetsTravelWhole()
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var old = (await Store.IssueAsync(tenant, null, "old", AccessLevel.Read, ResourceScope.Tenant, DateTimeOffset.UtcNow.AddDays(-200), [], false, admin, CancellationToken.None))!;
        await Store.RevokeAsync(tenant, old.CredentialId, admin, CancellationToken.None);
        var before = await Store.VersionAsync(CancellationToken.None);

        await Store.PruneRevocationsAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await Store.SetRulesAsync(tenant, "b", null, [new PolicyRule(tenant, "b", null, PolicyEffect.Deny, PrincipalKind.Identity, "x")], admin, CancellationToken.None);
        var delta = await DeltaAsync(before);

        Assert.Contains(old.CredentialId, delta.DeletedCredentialIds);
        var set = Assert.Single(delta.RuleSets, r => r.TenantId == tenant);
        Assert.Equal(("b", (string?)null, PolicyEffect.Deny), (set.Bucket, set.Key, Assert.Single(set.Rules).Effect));
    }

    [Fact]
    public async Task ConcurrentMutations_ProduceGapFreeDeltas() // Arch §1.1 load-bearing invariant
    {
        var tenant = Tenant();
        var admin = await f.TenantAsync(tenant);
        var start = await Store.VersionAsync(CancellationToken.None);

        var issued = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Store.IssueAsync(tenant, null, $"p{i}", AccessLevel.Read, ResourceScope.Tenant, null, [], false, admin, CancellationToken.None)));

        // Walk deltas in small steps from the start, as a gateway polling mid-burst would: every credential appears.
        var seen = new HashSet<string>();
        var at = start;
        while (true)
        {
            var delta = await DeltaAsync(at);
            foreach (var c in delta.Credentials) seen.Add(c.CredentialId);
            if (delta.Version == at) break;
            at = Math.Min(delta.Version, at + 3);
        }

        Assert.Superset(seen, issued.Select(g => g!.CredentialId).ToHashSet());
    }

    [Fact]
    public async Task Snapshot_AndChanges_AreNeverCached()
    {
        using var http = f.Service();
        foreach (var path in new[] { "/internal/snapshot", "/internal/changes?since=0" })
        {
            var r = await http.GetAsync(path);
            Assert.Contains("no-store", r.Headers.CacheControl?.ToString() ?? "");
        }

        var snapshot = await http.GetFromJsonAsync<AuthoritySnapshot>("/internal/snapshot", Json);
        Assert.False(string.IsNullOrEmpty(snapshot!.Epoch));
    }
}
