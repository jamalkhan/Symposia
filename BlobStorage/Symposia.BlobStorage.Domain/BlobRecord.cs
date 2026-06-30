namespace Symposia.BlobStorage.Domain;

public enum BlobStatus
{
    Active,
    PendingGc,
    Orphaned,
}

/// <summary>
/// A row in a node's local manifest (Layer 1 of Requirements/BlobStorage/metadata-architecture.md).
/// This is the node's own record of what it holds — not the cluster-wide routing index.
/// </summary>
public sealed record BlobRecord(
    Cid Cid,
    long SizeBytes,
    string TenantId,
    string Bucket,
    string Key,
    IReadOnlyList<string> RegionTags,
    DateTimeOffset StoredAt,
    DateTimeOffset? ChecksumVerifiedAt,
    BlobStatus Status);
