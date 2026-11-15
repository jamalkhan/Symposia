using Symposia.BlobStorage.Domain.Credentials;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>The authorization decision and its check order (issue #3 FR4, FR6).</summary>
public sealed class AccessEvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");

    private static CredentialGrant Grant(AccessLevel level = AccessLevel.ReadWrite, ResourceScope? scope = null, DateTimeOffset? expires = null,
        string[]? regions = null, CredentialStatus status = CredentialStatus.Active, DateTimeOffset? revokedAt = null) =>
        new("cred1", "AK", "t1", "t1-default", "app@t1", level, scope ?? ResourceScope.Tenant, expires, regions ?? [], false, status, Now.AddDays(-1), revokedAt);

    private static AccessRequest Req(bool write, string? bucket, string? key, string? owner = "t1", string? prefix = null, params string[] regions) =>
        new(write, bucket, key, owner, prefix, regions.Length == 0 ? ["us-east"] : regions);

    private static AccessDecision Eval(CredentialGrant g, AccessRequest r, TenantStatus tenant = TenantStatus.Active, params PolicyRule[] rules) =>
        AccessEvaluator.Evaluate(g, tenant, r, rules, Now);

    [Fact]
    public void ReadPrefixCredential_ReadsInside_AndIsRefusedWritesThere() // AC1
    {
        var g = Grant(AccessLevel.Read, new ResourceScope("b", "photos/"));
        Assert.Equal(AccessDecision.Allowed, Eval(g, Req(false, "b", "photos/1.jpg")));
        Assert.Equal(AccessDecision.Allowed, Eval(g, Req(false, "b", null, prefix: "photos/2026/")));
        Assert.Equal(AccessDecision.InsufficientCredentialScope, Eval(g, Req(true, "b", "photos/1.jpg")));
        Assert.Equal(AccessDecision.InsufficientCredentialScope, Eval(g, Req(false, "b", "docs/x")));      // sibling prefix (FR4.15)
        Assert.Equal(AccessDecision.InsufficientCredentialScope, Eval(g, Req(false, "b", null, prefix: ""))); // listing outside
    }

    [Fact]
    public void PrefixCredential_CantPassABucketLevelWriteByCarryingAPrefix() // review: Lenny #1
    {
        var g = Grant(AccessLevel.ReadWrite, new ResourceScope("b", "uploads/"));
        // A bucket-level write (lifecycle, scheme, regions, delete bucket) never has a list prefix.
        Assert.Equal(AccessDecision.InsufficientCredentialScope, Eval(g, Req(true, "b", null, prefix: null)));
        Assert.Equal(AccessDecision.InsufficientCredentialScope, Eval(g, Req(false, "b", null, prefix: null)));
        Assert.Equal(AccessDecision.Allowed, Eval(Grant(scope: new ResourceScope("b")), Req(true, "b", null)));
    }

    [Fact]
    public void BucketScopedCredential_GetsTheSameRefusal_ForAnyOtherBucket_ExistingOrNot() // AC2
    {
        var g = Grant(scope: new ResourceScope("bucket-a"));
        Assert.Equal(AccessDecision.InsufficientCredentialScope, Eval(g, Req(false, "bucket-b", "k", owner: "t2")));
        Assert.Equal(AccessDecision.InsufficientCredentialScope, Eval(g, Req(false, "bucket-b", "k", owner: null)));
    }

    [Fact]
    public void TenantWideCredential_CrossTenant_IsDenied_WhetherOrNotTheBucketExists() // FR1.2/1.3
    {
        Assert.Equal(AccessDecision.CrossTenantAccessDenied, Eval(Grant(), Req(false, "theirs", "k", owner: "t2")));
        Assert.Equal(AccessDecision.CrossTenantAccessDenied, Eval(Grant(), Req(false, "nobodys", "k", owner: null)));
        Assert.Equal(AccessDecision.Allowed, Eval(Grant(), Req(true, "new-bucket", null, owner: null))); // creating one is fine
    }

    [Fact]
    public void Expired_RegionLocked_Suspended() // AC3, AC4, AC12
    {
        Assert.Equal(AccessDecision.CredentialExpired, Eval(Grant(expires: Now), Req(false, "b", "k")));
        Assert.Equal(AccessDecision.Allowed, Eval(Grant(expires: Now.AddSeconds(1)), Req(false, "b", "k")));
        Assert.Equal(AccessDecision.RegionLockViolation, Eval(Grant(regions: ["eu-west"]), Req(false, "b", "k", regions: "us-east")));
        Assert.Equal(AccessDecision.Allowed, Eval(Grant(regions: ["eu-west"]), Req(false, "b", "k", regions: "eu-west")));
        Assert.Equal(AccessDecision.TenantSuspended, Eval(Grant(), Req(false, "b", "k"), TenantStatus.Suspended));
    }

    [Fact]
    public void Revocation_IsCheckedFirst_AndMasksEverythingElse() // FR4.13, QA open question #1
    {
        var revokedAndExpired = Grant(status: CredentialStatus.Revoked, expires: Now.AddDays(-1));
        Assert.Equal(AccessDecision.CredentialRevoked, Eval(revokedAndExpired, Req(true, "theirs", "k", owner: "t2"), TenantStatus.Suspended));
        // A rotation grace: revoked at a future instant still works until then.
        Assert.Equal(AccessDecision.Allowed, Eval(Grant(revokedAt: Now.AddMinutes(5)), Req(false, "b", "k")));
        Assert.Equal(AccessDecision.CredentialRevoked, Eval(Grant(revokedAt: Now), Req(false, "b", "k")));
    }

    [Fact]
    public void ObjectAclDeny_Wins_OverScopeAndBucketAllow() // AC9, FR6.24
    {
        var g = Grant();
        var allowBucket = new PolicyRule("t1", "b", null, PolicyEffect.Allow, PrincipalKind.Credential, "cred1");
        var denyObject = new PolicyRule("t1", "b", "secret.txt", PolicyEffect.Deny, PrincipalKind.Identity, "app@t1");

        Assert.Equal(AccessDecision.AccessDenied, Eval(g, Req(false, "b", "secret.txt"), TenantStatus.Active, allowBucket, denyObject));
        Assert.Equal(AccessDecision.Allowed, Eval(g, Req(false, "b", "public.txt"), TenantStatus.Active, allowBucket, denyObject));
    }

    [Fact]
    public void BucketAllowList_AdmitsOnlyListedPrincipals()
    {
        var allowOther = new PolicyRule("t1", "b", null, PolicyEffect.Allow, PrincipalKind.Credential, "someone-else");
        Assert.Equal(AccessDecision.AccessDenied, Eval(Grant(), Req(false, "b", "k"), TenantStatus.Active, allowOther));
    }

    [Fact]
    public void Scopes_Nest_ForMinimumNecessaryAccess() // FR3.10
    {
        Assert.True(new ResourceScope("b", "a/b/").IsWithin(new ResourceScope("b", "a/")));
        Assert.True(new ResourceScope("b", null, "a/x").IsWithin(new ResourceScope("b", "a/")));
        Assert.False(new ResourceScope("b").IsWithin(new ResourceScope("b", "a/")));
        Assert.False(ResourceScope.Tenant.IsWithin(new ResourceScope("b")));
        Assert.False(new ResourceScope("c").IsWithin(new ResourceScope("b")));
    }
}
