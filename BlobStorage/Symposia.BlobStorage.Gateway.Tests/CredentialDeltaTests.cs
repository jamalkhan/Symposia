using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain.Credentials;
using Symposia.BlobStorage.Gateway.Auth;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Applying Authority deltas to the gateway cache (issue #245).</summary>
public sealed class CredentialDeltaTests
{
    private static CredentialGrant Grant(string id, string key, string? secret, CredentialStatus status = CredentialStatus.Active) =>
        new(id, key, "t", "t-default", "app", AccessLevel.Read, ResourceScope.Tenant, null, [], false, status, DateTimeOffset.UtcNow, Secret: secret);

    private static CredentialStore Cache(long version = 5, string epoch = "e1") 
    {
        var cache = new CredentialStore(Options.Create(new GatewayOptions()));
        cache.Apply(new AuthoritySnapshot(version, [new TenantState("t", TenantStatus.Active)], [Grant("c1", "AK1", "s1")], []) { Epoch = epoch });
        return cache;
    }

    private static AuthorityDelta Delta(long from, long to, string epoch = "e1", CredentialGrant[]? credentials = null, string[]? deleted = null,
        TenantState[]? tenants = null, RuleSet[]? rules = null) =>
        new(from, to, epoch, tenants ?? [], credentials ?? [], deleted ?? [], rules ?? []);

    [Fact]
    public void Revocation_KeepsTheCachedSecret_AndAppliesTheStatus()
    {
        var cache = Cache();
        Assert.Equal(CredentialStore.DeltaResult.Applied, cache.ApplyDelta(Delta(5, 6, credentials: [Grant("c1", "AK1", null, CredentialStatus.Revoked)])));

        Assert.Equal(CredentialStatus.Revoked, cache.Grant("AK1")!.Status);
        Assert.Equal("s1", cache.Grant("AK1")!.Secret);
        Assert.Equal(6, cache.Version);
    }

    [Fact]
    public void NewCredentialWithoutASecret_Gaps_AndOtherEpochs_NeedTheSnapshot()
    {
        Assert.Equal(CredentialStore.DeltaResult.NeedsSnapshot, Cache().ApplyDelta(Delta(5, 6, credentials: [Grant("c2", "AK2", null)])));
        Assert.Equal(CredentialStore.DeltaResult.NeedsSnapshot, Cache().ApplyDelta(Delta(6, 7)));
        Assert.Equal(CredentialStore.DeltaResult.NeedsSnapshot, Cache().ApplyDelta(Delta(5, 6, epoch: "e2")));
        Assert.Equal(CredentialStore.DeltaResult.Stale, Cache().ApplyDelta(Delta(4, 5)));
    }

    [Fact]
    public void Tombstones_TenantsAndRuleSets_Apply()
    {
        var cache = Cache();
        var rule = new PolicyRule("t", "b", null, PolicyEffect.Deny, PrincipalKind.Credential, "c1");
        cache.ApplyDelta(Delta(5, 6, credentials: [Grant("c2", "AK2", "s2")], tenants: [new TenantState("t", TenantStatus.Suspended)],
            rules: [new RuleSet("t", "b", null, [rule])]));
        cache.ApplyDelta(Delta(6, 7, deleted: ["c1"]));

        Assert.Null(cache.Grant("AK1"));
        Assert.Equal("s2", cache.Grant("AK2")!.Secret);
        Assert.Equal(TenantStatus.Suspended, cache.TenantStatus("t"));
        Assert.Equal(rule, Assert.Single(cache.Rules));

        cache.ApplyDelta(Delta(7, 8, rules: [new RuleSet("t", "b", null, [])]));
        Assert.Empty(cache.Rules);
    }

    [Fact]
    public void SnapshotFromARestoredDatabase_IsTakenEvenAtALowerVersion()
    {
        var cache = Cache(version: 50, epoch: "old");
        Assert.False(cache.Apply(new AuthoritySnapshot(40, [], [], []) { Epoch = "old" }));
        Assert.True(cache.Apply(new AuthoritySnapshot(40, [], [], []) { Epoch = "restored" }));
        Assert.Equal(40, cache.Version);
    }
}
