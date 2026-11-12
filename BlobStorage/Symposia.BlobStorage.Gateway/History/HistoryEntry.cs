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
    int Status,
    long? DurationMs = null);

/// <summary>One epoch's Blob Gateway factors (#221), in ScoringWeights order bar <see cref="Requests"/>.</summary>
public sealed record EpochFactors(uint UptimeBps, uint SuccessBps, long P95Ms, long BytesServed, long Requests);

/// <summary>Where an epoch's report is (#221). Terminal states are never revisited.</summary>
public enum EpochReportState
{
    Measured,
    Signed,
    Submitted,
    Terminal,
    Skipped,
}

/// <summary>A persisted epoch report: factors measured once at epoch end, then its submission state.</summary>
public sealed record EpochReportRow(ulong Epoch, long StartMs, long EndMs, EpochFactors Factors, EpochReportState State,
    string? Signature, string? TxHash, string? LastError, int Attempts, long UpdatedMs);

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
