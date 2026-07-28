namespace Symposia.Database.ComputeNode.Databases;

/// <summary>
/// Body of POST /databases — the local control API contract #95's orchestration is expected to call.
/// This shape is a forward proposal (see the architectural plan on issue #88); it is not yet a
/// ratified cross-issue contract.
/// </summary>
public sealed record PlaceDatabaseRequest(
    string TenantDatabaseId,
    string BlobBucketCredential,
    int PgVersion,
    string[] Extensions,
    string[] SafekeeperPeers,
    string? BlobBucketUrl = null);
