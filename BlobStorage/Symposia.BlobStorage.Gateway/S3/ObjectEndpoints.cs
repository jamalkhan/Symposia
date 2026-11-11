using Symposia.BlobStorage.Gateway.History;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Objects;
using Symposia.BlobStorage.Gateway.Quorum;

namespace Symposia.BlobStorage.Gateway.S3;

internal static class ObjectEndpoints
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        // PutObject / CopyObject (x-amz-copy-source present → copy path)
        app.MapPut("/{bucket}/{**key}", PutOrCopy);

        // GetObject
        app.MapGet("/{bucket}/{**key}", GetObject);

        // HeadObject
        app.MapMethods("/{bucket}/{**key}", ["HEAD"], HeadObject);

        // DeleteObject
        app.MapDelete("/{bucket}/{**key}", DeleteObject);
    }

    // ── PutObject / CopyObject ────────────────────────────────────────────────

    private static async Task<IResult> PutOrCopy(
        string bucket, string key, HttpContext ctx,
        GatewayMetadataStore store, QuorumWriter writer)
    {
        if (!TenantId(ctx, out var tenantId)) return S3Xml.AccessDenied();
        if (MultipartEndpoints.IsPartUpload(ctx.Request)) return await MultipartEndpoints.UploadPartAsync(bucket, key, ctx);

        // x-amz-copy-source header triggers CopyObject semantics.
        var copySource = ctx.Request.Headers["x-amz-copy-source"].FirstOrDefault();
        if (copySource is not null)
            return CopyObject(bucket, key, copySource, store, ctx);

        if (!store.BucketExists(bucket))
            return S3Xml.NoSuchBucket(bucket);

        var written = HistoryCapture.Mark(ctx, store.GetObject(bucket, key) is null ? HistoryOperation.Insert : HistoryOperation.Update, bucket, key, "s3");

        var contentType = ctx.Request.ContentType ?? "application/octet-stream";

        // Object bytes from the wire format (aws-chunked, #135), with chunk signatures and every
        // declared checksum verified as they stream (#139). A failed check throws before the body
        // ends, so the node writes are cancelled and nothing is committed.
        ObjectRecord? record;
        ChecksumVerifyingStream body;
        try
        {
            body = UploadBody.Open(ctx);
            record = await writer.WriteAsync(tenantId, bucket, key, contentType, body, ctx.RequestAborted);
        }
        catch (InvalidDataException ex)
        {
            return S3Xml.IncompleteBody(ex.Message);
        }
        catch (S3RequestRejectedException ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Symposia.BlobStorage.Gateway.S3.Upload")
                .LogWarning("Rejected PUT {Bucket}/{Key} from {AccessKey}: {Code} {Message}", bucket, key,
                    (ctx.Items[nameof(Auth.SigV4Context)] as Auth.SigV4Context)?.AccessKeyId, ex.Code, ex.Message);
            return S3Xml.Error(ex.Status, ex.Code, ex.Message);
        }

        foreach (var (algorithm, value) in body.Computed.Where(c => c.Key != "md5"))
        {
            ctx.Response.Headers[$"x-amz-checksum-{algorithm}"] = value;
        }

        if (record is null)
            return S3Xml.ServiceUnavailable();

        (written.Size, written.Etag, written.Cid) = (record.SizeBytes, record.ETag, record.Cid);
        ctx.Response.Headers.ETag = $"\"{record.Cid}\"";
        return Results.Ok();
    }

    // CopyObject: content-addressed store means no data movement.
    // We simply copy the metadata pointer (the CID is identical if content is identical,
    // which is the expected semantics). Different key → new metadata entry, same CID.
    private static IResult CopyObject(
        string destBucket, string destKey, string copySource, GatewayMetadataStore store, HttpContext ctx)
    {
        // copy-source format: /bucket/key or bucket/key (AWS SDK v4 percent-encodes slashes)
        // URL-decode first so %2F-encoded slashes become real separators before we split.
        var path = Uri.UnescapeDataString(copySource.TrimStart('/'));
        var slash = path.IndexOf('/');
        if (slash < 0) return S3Xml.BadRequest("Invalid x-amz-copy-source header.");

        var srcBucket = path[..slash];
        var srcKey = path[(slash + 1)..];

        // History (#204): a view of the source and a write of the destination, under one request ID.
        var read = HistoryCapture.Mark(ctx, HistoryOperation.View, srcBucket, srcKey, "s3");
        var src = store.GetObject(srcBucket, srcKey);
        if (src is null) return S3Xml.NoSuchKey(srcKey);

        var written = HistoryCapture.Mark(ctx, store.GetObject(destBucket, destKey) is null ? HistoryOperation.Insert : HistoryOperation.Update,
            destBucket, destKey, "s3");
        var dest = src with { Bucket = destBucket, Key = destKey, LastModified = DateTimeOffset.UtcNow };
        store.PutObject(dest);
        (read.Size, read.Etag, read.Cid) = (src.SizeBytes, src.ETag, src.Cid);
        (written.Size, written.Etag, written.Cid) = (dest.SizeBytes, dest.ETag, dest.Cid);

        return S3Xml.CopyObjectResponse(dest.ETag, dest.LastModified);
    }

    // ── GetObject ─────────────────────────────────────────────────────────────

    private static async Task<IResult> GetObject(
        string bucket, string key, HttpContext ctx, GatewayMetadataStore store, ObjectService objects)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();
        if (MultipartEndpoints.HasUploadId(ctx.Request)) return MultipartEndpoints.ListParts(bucket, key, ctx);

        var view = HistoryCapture.Mark(ctx, HistoryOperation.View, bucket, key, "s3");
        var record = store.GetObject(bucket, key);
        if (record is null) return S3Xml.NoSuchKey(key);

        return await objects.ServeAsync(record, ctx.Request.Headers.Range.FirstOrDefault(), ctx, view) == ServeOutcome.NoReplica
            ? S3Xml.ServiceUnavailable()
            : Results.Empty;
    }

    // ── HeadObject ────────────────────────────────────────────────────────────

    private static IResult HeadObject(
        string bucket, string key, HttpContext ctx, GatewayMetadataStore store)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();

        var record = store.GetObject(bucket, key);
        if (record is null) return S3Xml.NoSuchKey(key);

        ctx.Response.Headers.ETag = $"\"{record.ETag}\"";
        ctx.Response.Headers.LastModified = record.LastModified.ToString("R");
        ctx.Response.ContentType = record.ContentType;
        ctx.Response.ContentLength = record.SizeBytes;
        return Results.Ok();
    }

    // ── DeleteObject ──────────────────────────────────────────────────────────

    private static IResult DeleteObject(string bucket, string key, HttpContext ctx, ObjectService objects)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();
        if (MultipartEndpoints.HasUploadId(ctx.Request)) return MultipartEndpoints.Abort(bucket, key, ctx);

        var deleted = HistoryCapture.Mark(ctx, HistoryOperation.Delete, bucket, key, "s3");
        if (objects.Delete(bucket, key) is { } record)
            (deleted.Size, deleted.Etag, deleted.Cid) = (record.SizeBytes, record.ETag, record.Cid);
        else
            deleted.Outcome = "NotFound";

        // S3 DeleteObject is always 204, even if the key did not exist.
        return Results.StatusCode(204);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TenantId(HttpContext ctx, out string tenantId)
    {
        tenantId = ctx.Items["TenantId"] as string ?? "";
        return tenantId.Length > 0;
    }
}
