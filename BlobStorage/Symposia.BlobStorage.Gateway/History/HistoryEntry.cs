namespace Symposia.BlobStorage.Gateway.History;

/// <summary>One recorded object operation (issue #204).</summary>
public sealed record HistoryEntry(
    long Id,
    DateTimeOffset Timestamp,
    string Operation,
    string Bucket,
    string Key,
    string Api,
    long? Size,
    string? Etag,
    string? Cid,
    string? ClientIp,
    string? AccessKeyId,
    string? TenantId,
    string? UserAgent,
    string RequestId,
    string Outcome,
    int Status);

/// <summary>Operation types recorded per object.</summary>
public static class HistoryOperation
{
    public const string Insert = "insert";
    public const string Update = "update";
    public const string View = "view";
    public const string Delete = "delete";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Insert, Update, View, Delete], StringComparer.Ordinal);
}

/// <summary>
/// An operation a handler is performing, filled in as it learns the size, ETag and CID, and recorded
/// by <see cref="HistoryCapture"/> once the response completes, with its final status. An explicit
/// <see cref="Outcome"/> overrides the one derived from the status (e.g. a 204 delete of a missing key).
/// </summary>
public sealed class PendingOperation(string operation, string bucket, string key, string api)
{
    public string Operation { get; set; } = operation;
    public string Bucket { get; } = bucket;
    public string Key { get; } = key;
    public string Api { get; } = api;
    public long? Size { get; set; }
    public string? Etag { get; set; }
    public string? Cid { get; set; }
    public string? Outcome { get; set; }
}
