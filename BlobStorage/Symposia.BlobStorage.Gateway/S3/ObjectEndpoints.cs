using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Objects;

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
        GatewayMetadataStore store, ObjectService objects)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();
        if (MultipartEndpoints.IsPartUpload(ctx.Request)) return await MultipartEndpoints.UploadPartAsync(bucket, key, ctx);

        // x-amz-copy-source header triggers CopyObject semantics.
        var copySource = ctx.Request.Headers["x-amz-copy-source"].FirstOrDefault();
        if (copySource is not null)
            return CopyObject(bucket, key, copySource, objects, ctx);

        if (!store.BucketExists(bucket))
            return S3Xml.NoSuchBucket(bucket);

        var contentType = ctx.Request.ContentType ?? "application/octet-stream";

        // Object bytes from the wire format (aws-chunked, #135), with chunk signatures and every
        // declared checksum verified as they stream (#139). A failed check throws before the body
        // ends, so the node writes are cancelled and nothing is committed.
        ObjectRecord? record;
        ChecksumVerifyingStream body;
        try
        {
            body = UploadBody.Open(ctx);
            record = await objects.PutAsync(ctx, bucket, key, contentType, body);
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

        ctx.Response.Headers.ETag = $"\"{record.Cid}\"";
        return Results.Ok();
    }

    // CopyObject: content-addressed store means no data movement.
    // We simply copy the metadata pointer (the CID is identical if content is identical,
    // which is the expected semantics). Different key → new metadata entry, same CID.
    private static IResult CopyObject(
        string destBucket, string destKey, string copySource, ObjectService objects, HttpContext ctx)
    {
        // copy-source format: /bucket/key or bucket/key (AWS SDK v4 percent-encodes slashes)
        // URL-decode first so %2F-encoded slashes become real separators before we split.
        var path = Uri.UnescapeDataString(copySource.TrimStart('/'));
        var slash = path.IndexOf('/');
        if (slash < 0) return S3Xml.BadRequest("Invalid x-amz-copy-source header.");

        var srcBucket = path[..slash];
        var srcKey = path[(slash + 1)..];

        var dest = objects.Copy(ctx, srcBucket, srcKey, destBucket, destKey);
        if (dest is null) return S3Xml.NoSuchKey(srcKey);

        return S3Xml.CopyObjectResponse(dest.ETag, dest.LastModified);
    }

    // ── GetObject ─────────────────────────────────────────────────────────────

    private static async Task<IResult> GetObject(
        string bucket, string key, HttpContext ctx, ObjectService objects)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();
        if (MultipartEndpoints.HasUploadId(ctx.Request)) return MultipartEndpoints.ListParts(bucket, key, ctx);

        return await objects.GetAsync(ctx, bucket, key) switch
        {
            ServeOutcome.NotFound => S3Xml.NoSuchKey(key),
            ServeOutcome.NoReplica => S3Xml.ServiceUnavailable(),
            _ => Results.Empty,
        };
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

        objects.Delete(ctx, bucket, key);

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
