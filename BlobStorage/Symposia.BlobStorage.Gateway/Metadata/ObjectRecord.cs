namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>
/// One row in the gateway's derived metadata index.
/// See Requirements/BlobStorage/metadata-architecture.md — this is the off-chain projection
/// that backs ListObjects, HeadObject, and CopyObject. It can be rebuilt from node manifests.
/// </summary>
public sealed record ObjectRecord(
    string Bucket,
    string Key,
    string Cid,
    long SizeBytes,
    string ContentType,
    DateTimeOffset LastModified,
    IReadOnlyList<string> NodeIds,
    string? EtagOverride = null)
{
    /// <summary>
    /// The client-visible ETag: the CID, except for multipart-completed objects, whose S3-convention
    /// ETag (<c>md5-of-part-etags-N</c>) is stored separately so the CID stays a true content hash (#5).
    /// </summary>
    public string ETag => EtagOverride ?? Cid;

    /// <summary>Whether every target copy has confirmed, or only the write quorum so far (#7 FR5).</summary>
    public ReplicationState Replication { get; init; } = ReplicationState.Full;

    /// <summary>Copies repair maintains for this object (#7/#8: from its region assignment at write time); 0 = the global floor.</summary>
    public int TargetCopies { get; init; }

    /// <summary>The region assignment this object was placed under (#7 FR6 metadata; empty = global).</summary>
    public IReadOnlyList<string> Regions { get; init; } = [];
}
