using System.Text;
using System.Text.RegularExpressions;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Objects;
using Symposia.BlobStorage.Gateway.Quorum;
using Symposia.BlobStorage.Gateway.S3;

namespace Symposia.BlobStorage.Gateway.Api;

/// <summary>
/// The Symposia-native blob API (issue #203): JSON under <c>/v1</c>, beside the S3 surface and over
/// the same buckets, objects and write/read paths, so an object written through either API reads
/// back through the other. It's the home for what S3 doesn't model: <c>?meta</c> (CID and replica
/// placement) now, operation history (#204) next. <c>/v1</c> can't collide with S3 paths because
/// S3 bucket names are at least three characters. Auth is SigV4 with the gateway's access keys.
/// </summary>
public static partial class BlobApiEndpoints
{
    public const string Prefix = "/v1";
    public const string OpenApiPath = "/v1/openapi.json";
    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;

    public static void Map(IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup(Prefix);
        v1.MapGet("/openapi.json", () => Results.Text(OpenApi.Value, "application/json"));

        v1.MapGet("/buckets", (GatewayMetadataStore store) =>
            Results.Ok(new { buckets = store.ListBuckets().Select(b => new { name = b.Name, createdAt = b.CreatedAt }) }));

        v1.MapPut("/buckets/{bucket}", (string bucket, GatewayMetadataStore store) =>
        {
            if (!IsValidBucketName(bucket))
            {
                return Error(400, "InvalidBucketName", "Bucket names are 3-63 lowercase letters, digits, dots and hyphens, starting and ending with a letter or digit.");
            }

            store.CreateBucket(bucket); // idempotent, as in S3
            return Results.Ok(new { name = bucket });
        });

        v1.MapDelete("/buckets/{bucket}", (string bucket, GatewayMetadataStore store) =>
            !store.BucketExists(bucket) ? NoSuchBucket(bucket)
            : !store.DeleteBucket(bucket) ? Error(409, "BucketNotEmpty", "The bucket you tried to delete is not empty.")
            : Results.NoContent());

        v1.MapGet("/buckets/{bucket}/objects", (string bucket, string? prefix, string? cursor, string? limit, GatewayMetadataStore store) =>
        {
            if (!store.BucketExists(bucket))
            {
                return NoSuchBucket(bucket);
            }

            var max = DefaultLimit;
            if (limit is not null && (!int.TryParse(limit, out max) || max is < 1 or > MaxLimit))
            {
                return Error(400, "InvalidArgument", $"limit must be an integer from 1 to {MaxLimit}.");
            }

            string? after = null;
            if (cursor is not null && !TryDecodeCursor(cursor, out after))
            {
                return Error(400, "InvalidArgument", "cursor is not one this API returned.");
            }

            var (objects, truncated, next) = store.ListObjects(bucket, prefix ?? "", max, after);
            return Results.Ok(new
            {
                objects = objects.Select(Describe),
                nextCursor = truncated && next is not null ? EncodeCursor(next) : null,
            });
        });

        v1.MapPut("/buckets/{bucket}/objects/{**key}", async (string bucket, string key, HttpContext ctx, GatewayMetadataStore store, QuorumWriter writer) =>
        {
            if (!store.BucketExists(bucket))
            {
                return NoSuchBucket(bucket);
            }

            ObjectRecord? record;
            try
            {
                record = await writer.WriteAsync(TenantId(ctx), bucket, key, ctx.Request.ContentType ?? "application/octet-stream",
                    ctx.Request.Body, ctx.RequestAborted);
            }
            catch (S3RequestRejectedException ex)
            {
                return Error(ex.Status, ex.Code, ex.Message);
            }

            return record is null ? Unavailable() : Results.Ok(Describe(record));
        });

        v1.MapGet("/buckets/{bucket}/objects/{**key}", async (string bucket, string key, HttpContext ctx, GatewayMetadataStore store, ObjectService objects) =>
        {
            var record = store.GetObject(bucket, key);
            if (ctx.Request.Query.ContainsKey("meta"))
            {
                return record is null ? NoSuchKey(key) : Results.Ok(new
                {
                    bucket = record.Bucket, key = record.Key, size = record.SizeBytes, contentType = record.ContentType,
                    etag = record.ETag, cid = record.Cid, lastModified = record.LastModified, replicas = record.NodeIds,
                });
            }

            if (record is null)
            {
                return NoSuchKey(key);
            }

            return await objects.ServeAsync(record, ctx.Request.Headers.Range.FirstOrDefault(), ctx) == ServeOutcome.NoReplica
                ? Unavailable()
                : Results.Empty;
        });

        v1.MapMethods("/buckets/{bucket}/objects/{**key}", ["HEAD"], (string bucket, string key, HttpContext ctx, GatewayMetadataStore store) =>
        {
            if (store.GetObject(bucket, key) is not { } record)
            {
                return Results.NotFound();
            }

            ctx.Response.Headers.ETag = $"\"{record.ETag}\"";
            ctx.Response.Headers.LastModified = record.LastModified.ToString("R");
            ctx.Response.ContentType = record.ContentType;
            ctx.Response.ContentLength = record.SizeBytes;
            return Results.Ok();
        });

        v1.MapDelete("/buckets/{bucket}/objects/{**key}", (string bucket, string key, ObjectService objects) =>
        {
            objects.Delete(bucket, key); // idempotent, as in S3
            return Results.NoContent();
        });
    }

    /// <summary>A JSON error, <c>{"error":{"code","message","requestId"}}</c>, with S3's codes and statuses.</summary>
    public static IResult Error(int status, string code, string message) => new ErrorResult(status, code, message);

    private static IResult NoSuchBucket(string bucket) => Error(404, "NoSuchBucket", $"The bucket '{bucket}' does not exist.");

    private static IResult NoSuchKey(string key) => Error(404, "NoSuchKey", $"The key '{key}' does not exist.");

    private static IResult Unavailable() => Error(503, "ServiceUnavailable", "No storage node could serve the request; try again.");

    private static object Describe(ObjectRecord r) => new
    {
        bucket = r.Bucket, key = r.Key, size = r.SizeBytes, contentType = r.ContentType, etag = r.ETag, cid = r.Cid, lastModified = r.LastModified,
    };

    private static string TenantId(HttpContext ctx) => ctx.Items["TenantId"] as string ?? "";

    // Opaque to callers: the last key returned, base64url-encoded.
    private static string EncodeCursor(string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeCursor(string cursor, out string? key)
    {
        key = null;
        var b64 = cursor.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try
        {
            key = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static bool IsValidBucketName(string name) =>
        name.Length is >= 3 and <= 63 && BucketName().IsMatch(name) && !name.Contains("..") && !IpAddress().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*[a-z0-9]$")]
    private static partial Regex BucketName();

    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$")]
    private static partial Regex IpAddress();

    private static readonly Lazy<string> OpenApi = new(() =>
    {
        using var stream = typeof(BlobApiEndpoints).Assembly.GetManifestResourceStream("Symposia.BlobStorage.Gateway.Api.openapi.json")!;
        return new StreamReader(stream).ReadToEnd();
    });

    private sealed class ErrorResult(int status, string code, string message) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = status;
            return ctx.Response.WriteAsJsonAsync(new { error = new { code, message, requestId = RequestIds.Of(ctx) } });
        }
    }
}
