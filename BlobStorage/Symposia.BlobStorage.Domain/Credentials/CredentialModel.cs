namespace Symposia.BlobStorage.Domain.Credentials;

// Shared by the Credential Authority (source of truth) and every gateway (cache and enforce), so the two
// can't drift on what a credential means (BlobStorage #3, Requirements/BlobStorage/multi-tenancy-and-credentials.md).

public enum AccessLevel
{
    /// <summary>GetObject, HeadObject, ListObjects.</summary>
    Read,

    /// <summary>Everything, delete included.</summary>
    ReadWrite,
}

public enum CredentialStatus { Active, Revoked }

public enum TenantStatus { Active, Suspended, SoftDeleted, Purged }

/// <summary>
/// What a credential reaches (#3 FR3.8): the whole tenant (all null), a bucket, a folder prefix in a bucket,
/// or one object. Scopes nest, which is what stops a credential minting a broader one (FR3.10).
/// </summary>
public sealed record ResourceScope(string? Bucket = null, string? Prefix = null, string? Key = null)
{
    public static readonly ResourceScope Tenant = new();

    public bool IsTenantWide => Bucket is null;

    /// <summary>Whether a request on <paramref name="bucket"/>/<paramref name="key"/> falls inside this scope (key null = a bucket-level call).</summary>
    public bool Covers(string? bucket, string? key, string? listPrefix = null)
    {
        if (Bucket is null) return true;
        if (bucket is null || !string.Equals(Bucket, bucket, StringComparison.Ordinal)) return false;
        if (Key is not null) return key == Key;
        if (Prefix is null) return true;
        if (key is not null) return key.StartsWith(Prefix, StringComparison.Ordinal);
        // A bucket-level call is covered only when it is a listing inside the prefix (listPrefix is null for
        // any other bucket-level call: config writes, bucket deletion, HEAD), never by a prefix it merely carries.
        return listPrefix is not null && listPrefix.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>Whether this scope is no broader than <paramref name="outer"/> (minimum necessary access).</summary>
    public bool IsWithin(ResourceScope outer)
    {
        if (outer.Bucket is null) return true;
        if (Bucket != outer.Bucket) return false;
        if (outer.Key is not null) return Key == outer.Key;
        if (outer.Prefix is null) return true;
        return Key is not null ? Key.StartsWith(outer.Prefix, StringComparison.Ordinal) : Prefix?.StartsWith(outer.Prefix, StringComparison.Ordinal) == true;
    }

    public override string ToString() => Bucket is null ? "*" : Key is not null ? $"{Bucket}/{Key}" : $"{Bucket}/{Prefix}*";
}

/// <summary>One credential as gateways cache it (#3 FR3). <see cref="Secret"/> travels only from Authority to gateway.</summary>
public sealed record CredentialGrant(
    string CredentialId,
    string AccessKeyId,
    string TenantId,
    string AccountId,
    string Identity,
    AccessLevel AccessLevel,
    ResourceScope Scope,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> RegionLock,
    bool CanManage,
    CredentialStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt = null,
    string? RotatedFrom = null,
    string? RotatedTo = null,
    string? Secret = null);

public sealed record TenantState(string TenantId, TenantStatus Status);

public enum PolicyEffect { Allow, Deny }

/// <summary>Who a policy rule is about (#3 FR6.22): one credential, a whole tenant, an access level or an identity.</summary>
public enum PrincipalKind { Credential, Tenant, AccessLevel, Identity }

/// <summary>A bucket policy rule (Key null) or an object ACL rule (#3 FR6).</summary>
public sealed record PolicyRule(string TenantId, string Bucket, string? Key, PolicyEffect Effect, PrincipalKind Kind, string Principal)
{
    public bool Matches(CredentialGrant c) => Kind switch
    {
        PrincipalKind.Credential => c.CredentialId == Principal,
        PrincipalKind.Tenant => c.TenantId == Principal,
        PrincipalKind.AccessLevel => c.AccessLevel.ToString().Equals(Principal, StringComparison.OrdinalIgnoreCase),
        PrincipalKind.Identity => c.Identity == Principal,
        _ => false,
    };
}

/// <summary>Everything a gateway needs to enforce, at one version of the Authority's state.</summary>
public sealed record AuthoritySnapshot(long Version, IReadOnlyList<TenantState> Tenants, IReadOnlyList<CredentialGrant> Credentials, IReadOnlyList<PolicyRule> Rules);

/// <summary>A data-plane request, as authorization sees it.</summary>
public sealed record AccessRequest(bool IsWrite, string? Bucket, string? Key, string? BucketOwner, string? ListPrefix, IReadOnlyList<string> Regions);

/// <summary>Why a request was refused (#3 FR4 error codes); <see cref="Allowed"/> when it wasn't.</summary>
public enum AccessDecision
{
    Allowed,
    CredentialRevoked,
    TenantSuspended,
    CredentialExpired,
    RegionLockViolation,
    CrossTenantAccessDenied,
    InsufficientCredentialScope,
    AccessDenied,
}

/// <summary>
/// The authorization check, in the order #3 FR4.13 fixes: revocation, expiry, region lock, access level,
/// resource scope, then bucket policy and object ACL with explicit deny winning (FR6.24). Tenant isolation
/// (FR1.2) and suspension (FR2.6) are checked right after revocation, at request time. Fail-closed throughout;
/// the first failing check decides, and a revoked credential learns nothing else about itself.
/// </summary>
public static class AccessEvaluator
{
    public static AccessDecision Evaluate(CredentialGrant credential, TenantStatus tenant, AccessRequest request, IEnumerable<PolicyRule> rules, DateTimeOffset now)
    {
        if (credential.Status == CredentialStatus.Revoked || credential.RevokedAt is { } revoked && revoked <= now)
            return AccessDecision.CredentialRevoked;
        if (tenant != TenantStatus.Active)
            return AccessDecision.TenantSuspended;
        if (credential.ExpiresAt is { } expires && expires <= now)
            return AccessDecision.CredentialExpired;
        if (credential.RegionLock.Count > 0 && request.Regions.Any(r => !credential.RegionLock.Contains(r, StringComparer.OrdinalIgnoreCase)))
            return AccessDecision.RegionLockViolation;
        if (request.Bucket is not null && request.BucketOwner != credential.TenantId && !(request.BucketOwner is null && request.IsWrite && request.Key is null))
            // Another tenant's bucket, or one that doesn't exist: the same answer either way (FR1.3). Creating an unowned bucket is allowed.
            return credential.Scope.IsTenantWide ? AccessDecision.CrossTenantAccessDenied : AccessDecision.InsufficientCredentialScope;
        if (request.IsWrite && credential.AccessLevel != AccessLevel.ReadWrite)
            return AccessDecision.InsufficientCredentialScope;
        if (!credential.Scope.Covers(request.Bucket, request.Key, request.ListPrefix))
            return AccessDecision.InsufficientCredentialScope;

        if (request.Bucket is null)
            return AccessDecision.Allowed;
        var applicable = rules.Where(r => r.Bucket == request.Bucket && (r.Key is null || r.Key == request.Key) && r.Matches(credential)).ToList();
        if (applicable.Any(r => r.Effect == PolicyEffect.Deny))
            return AccessDecision.AccessDenied; // explicit deny at any layer wins
        // A bucket or object with allow-lists admits only listed principals.
        var bucketAllows = rules.Where(r => r.Bucket == request.Bucket && r.Key is null && r.Effect == PolicyEffect.Allow).ToList();
        var objectAllows = rules.Where(r => r.Bucket == request.Bucket && r.Key == request.Key && r.Key is not null && r.Effect == PolicyEffect.Allow).ToList();
        if (bucketAllows.Count > 0 && !bucketAllows.Any(r => r.Matches(credential)))
            return AccessDecision.AccessDenied;
        if (objectAllows.Count > 0 && !objectAllows.Any(r => r.Matches(credential)))
            return AccessDecision.AccessDenied;
        return AccessDecision.Allowed;
    }
}

/// <summary>One data-plane use, as a gateway reports it to the Authority's audit log (#3 FR8.28).</summary>
public sealed record UseReport(string CredentialId, string Action, string? Resource, string? SourceIp, string? Region, string GatewayId,
    string Outcome, string? Reason, DateTimeOffset At);

/// <summary>
/// A batch of uses with an ID the Authority records once: a batch resent after a lost acknowledgement is not
/// appended to the hash chain twice (review: Frank #7).
/// </summary>
public sealed record UseBatch(string GatewayId, string BatchId, IReadOnlyList<UseReport> Uses);
