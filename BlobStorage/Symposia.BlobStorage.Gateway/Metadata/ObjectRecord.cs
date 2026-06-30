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
    IReadOnlyList<string> NodeIds);
