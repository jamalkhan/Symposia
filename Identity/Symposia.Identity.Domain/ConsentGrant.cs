namespace Symposia.Identity.Domain;

/// <summary>
/// Read-model projection of a consent grant, matching the schema in
/// Requirements/Identity/user-data-ownership.md § Permission Grants. The
/// chain (<c>ConsentRegistry</c>) is authoritative; this is what the Identity
/// Gateway's fast read API (FR7, AC5) serves.
/// </summary>
public sealed record ConsentGrant(
    Guid IdentityId,
    string MarketerTenantId,
    IReadOnlyList<Permission> Permissions,
    DateTimeOffset GrantedAt,
    string GrantSource,
    string GrantWording,
    IReadOnlyList<Permission> RevokedPermissions,
    DateTimeOffset? RevokedAt);

/// <summary>
/// Read-model projection of an issued capability token, traceable back to its
/// originating consent grant (AC4).
/// </summary>
public sealed record CapabilityToken(
    ulong TokenId,
    Guid IdentityId,
    string MarketerTenantId,
    Permission Permission,
    DateTimeOffset IssuedAt,
    DateTimeOffset ConsentGrantedAt);
