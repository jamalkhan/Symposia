using System.Xml.Linq;
using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.S3;

/// <summary>
/// Builders for S3-compatible XML responses.
/// All responses use the s3.amazonaws.com/doc/2006-03-01/ namespace so existing S3 SDKs
/// can parse them without modification (Requirements/BlobStorage/storage-interfaces.md).
/// </summary>
internal static class S3Xml
{
    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    // ── Error responses ──────────────────────────────────────────────────────

    internal static IResult AccessDenied() =>
        ErrorXml(403, "AccessDenied", "Access Denied");

    internal static IResult NoSuchBucket(string bucket) =>
        ErrorXml(404, "NoSuchBucket", $"The specified bucket does not exist: {bucket}");

    internal static IResult NoSuchKey(string key) =>
        ErrorXml(404, "NoSuchKey", $"The specified key does not exist: {key}");

    internal static IResult BucketNotEmpty() =>
        ErrorXml(409, "BucketNotEmpty", "The bucket you tried to delete is not empty.");

    internal static IResult IncompleteBody(string message) => ErrorXml(400, "IncompleteBody", message);

    internal static IResult Error(int status, string code, string message) => ErrorXml(status, code, message);

    /// <summary>Seconds a client should wait before retrying a 503 (#1 FR-18).</summary>
    internal const int RetryAfterSeconds = 5;

    internal static IResult ServiceUnavailable() =>
        WithRetryAfter(ErrorXml(503, "ServiceUnavailable",
            "No replica of this object is reachable right now; it is temporarily unavailable. Retry shortly."));

    /// <summary>
    /// Every replica reports the blob missing (#8 FR7.2): permanently lost rather than temporarily
    /// unavailable. Still 503, as repair may yet restore it from a copy outside the metadata's view.
    /// </summary>
    internal static IResult DataLost() =>
        ErrorXml(503, "ObjectDataLost",
            "Every replica of this object reports it missing; the data is lost, not temporarily unavailable.");

    /// <summary>Adds <c>Retry-After</c> to a 503 (#1 FR-18).</summary>
    internal static IResult WithRetryAfter(IResult inner) => new RetryAfterResult(inner);

    private sealed class RetryAfterResult(IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
            return inner.ExecuteAsync(ctx);
        }
    }

    /// <summary>
    /// Write quorum not reached (#199): too few storage nodes took the write, typically because some
    /// are read-only for disk space. SlowDown tells S3 clients to back off and retry; a retry skips
    /// the nodes that refused.
    /// </summary>
    internal static IResult SlowDown() =>
        WithRetryAfter(ErrorXml(503, "SlowDown",
            "Write quorum was not reached: not enough storage nodes accepted the write. Retry shortly."));

    internal static IResult BadRequest(string message) =>
        ErrorXml(400, "BadRequest", message);

    private static IResult ErrorXml(int status, string code, string message)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("Error",
                new XElement("Code", code),
                new XElement("Message", message),
                new XElement("RequestId", Guid.NewGuid().ToString("N"))));
        return Results.Content(doc.ToString(), "application/xml", statusCode: status);
    }

    // ── Bucket responses ─────────────────────────────────────────────────────

    internal static IResult ListObjectsResponse(
        string bucket, string prefix, bool listV2,
        IReadOnlyList<ObjectRecord> objects, bool isTruncated, string? nextToken)
    {
        var root = new XElement(Ns + "ListBucketResult",
            new XElement(Ns + "Name", bucket),
            new XElement(Ns + "Prefix", prefix),
            new XElement(Ns + "IsTruncated", isTruncated ? "true" : "false"),
            new XElement(Ns + "MaxKeys", "1000"));

        if (listV2)
            root.Add(new XElement(Ns + "KeyCount", objects.Count));
        else
            root.Add(new XElement(Ns + "Marker", ""));

        if (isTruncated && nextToken is not null)
        {
            var elem = listV2 ? Ns + "NextContinuationToken" : Ns + "NextMarker";
            root.Add(new XElement(elem, nextToken));
        }

        foreach (var obj in objects)
        {
            root.Add(new XElement(Ns + "Contents",
                new XElement(Ns + "Key", obj.Key),
                new XElement(Ns + "LastModified", obj.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                new XElement(Ns + "ETag", $"\"{obj.ETag}\""),
                new XElement(Ns + "Size", obj.SizeBytes),
                new XElement(Ns + "StorageClass", "STANDARD")));
        }

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        return Results.Content(doc.ToString(), "application/xml");
    }

    // ── Object responses ─────────────────────────────────────────────────────

    internal static IResult CopyObjectResponse(string cid, DateTimeOffset lastModified)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("CopyObjectResult",
                new XElement("LastModified", lastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                new XElement("ETag", $"\"{cid}\"")));
        return Results.Content(doc.ToString(), "application/xml");
    }
}
