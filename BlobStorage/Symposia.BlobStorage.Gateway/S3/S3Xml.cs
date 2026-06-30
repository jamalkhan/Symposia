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

    internal static IResult ServiceUnavailable() =>
        ErrorXml(503, "ServiceUnavailable",
            "Write quorum was not reached. Not enough storage nodes confirmed the write.");

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
                new XElement(Ns + "ETag", $"\"{obj.Cid}\""),
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
