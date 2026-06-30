using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.S3;

internal static class BucketEndpoints
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        // PUT /{bucket}  — create bucket
        app.MapPut("/{bucket}", CreateBucket);

        // DELETE /{bucket}  — delete bucket
        app.MapDelete("/{bucket}", DeleteBucket);

        // HEAD /{bucket}  — check bucket exists
        app.MapMethods("/{bucket}", ["HEAD"], HeadBucket);

        // GET /{bucket}  — list objects (ListObjectsV1 and V2)
        app.MapGet("/{bucket}", ListObjects);
    }

    private static IResult CreateBucket(
        string bucket, HttpContext ctx, GatewayMetadataStore store)
    {
        if (!TenantId(ctx, out var tenantId)) return S3Xml.AccessDenied();

        // S3 CreateBucket is idempotent for the same owner — just ensure it exists.
        store.CreateBucket(bucket);

        ctx.Response.Headers.Location = $"/{bucket}";
        return Results.Ok();
    }

    private static IResult DeleteBucket(
        string bucket, HttpContext ctx, GatewayMetadataStore store)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();

        if (!store.BucketExists(bucket))
            return S3Xml.NoSuchBucket(bucket);

        if (!store.DeleteBucket(bucket))
            return S3Xml.BucketNotEmpty();

        return Results.StatusCode(204);
    }

    private static IResult HeadBucket(
        string bucket, HttpContext ctx, GatewayMetadataStore store)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();
        return store.BucketExists(bucket) ? Results.Ok() : S3Xml.NoSuchBucket(bucket);
    }

    private static IResult ListObjects(
        string bucket, HttpContext ctx, GatewayMetadataStore store)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();

        if (!store.BucketExists(bucket))
            return S3Xml.NoSuchBucket(bucket);

        var q = ctx.Request.Query;
        var listV2 = q["list-type"] == "2";
        var prefix = q["prefix"].FirstOrDefault() ?? "";
        var maxKeys = int.TryParse(q["max-keys"], out var mk) ? Math.Clamp(mk, 1, 1000) : 1000;
        var token = listV2
            ? q["continuation-token"].FirstOrDefault()
            : q["marker"].FirstOrDefault();

        var (objects, isTruncated, nextToken) = store.ListObjects(bucket, prefix, maxKeys, token);
        return S3Xml.ListObjectsResponse(bucket, prefix, listV2, objects, isTruncated, nextToken);
    }

    private static bool TenantId(HttpContext ctx, out string tenantId)
    {
        tenantId = ctx.Items["TenantId"] as string ?? "";
        return tenantId.Length > 0;
    }
}
