using System.Text;
using Symposia.BlobStorage.Gateway.History;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Objects;
using Symposia.BlobStorage.Gateway.S3;

namespace Symposia.BlobStorage.Gateway.Api;

/// <summary>
/// The Symposia-native blob API (issue #203): JSON under <c>/v1</c>, beside the S3 surface and over
/// the same buckets, objects and write/read paths, so an object written through either API reads
/// back through the other. It's the home for what S3 doesn't model: <c>?meta</c> (CID and replica
/// placement) now, operation history (#204) next. <c>/v1</c> can't collide with S3 paths because
/// S3 bucket names are at least three characters. Auth is SigV4 with the gateway's access keys.
/// </summary>
public static class BlobApiEndpoints
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
            if (!BucketNames.IsValid(bucket))
            {
                return Error(400, "InvalidBucketName", BucketNames.Rule);
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

        v1.MapPut("/buckets/{bucket}/objects/{**key}", async (string bucket, string key, HttpContext ctx, GatewayMetadataStore store, ObjectService objects) =>
        {
            if (!store.BucketExists(bucket))
            {
                return NoSuchBucket(bucket);
            }

            ObjectRecord? record;
            try
            {
                // The same body pipeline as S3 PUT: aws-chunked decoding, chunk signatures and every
                // declared payload checksum (x-amz-content-sha256 included) verified as it streams.
                record = await objects.PutAsync(ctx, bucket, key, ctx.Request.ContentType ?? "application/octet-stream", UploadBody.Open(ctx));
            }
            catch (InvalidDataException ex)
            {
                return Error(400, "IncompleteBody", ex.Message);
            }
            catch (S3RequestRejectedException ex)
            {
                return Error(ex.Status, ex.Code, ex.Message);
            }

            return record is null ? Unavailable() : Results.Ok(Describe(record));
        });

        v1.MapGet("/buckets/{bucket}/objects/{**key}", async (string bucket, string key, HttpContext ctx, GatewayMetadataStore store, ObjectService objects,
            HistoryStore history) =>
        {
            if (ctx.Request.Query.ContainsKey("history"))
            {
                return History(bucket, key, ctx.Request.Query, history);
            }

            if (ctx.Request.Query.ContainsKey("meta"))
            {
                return store.GetObject(bucket, key) is not { } record ? NoSuchKey(key) : Results.Ok(new
                {
                    bucket = record.Bucket, key = record.Key, size = record.SizeBytes, contentType = record.ContentType,
                    etag = record.ETag, cid = record.Cid, lastModified = record.LastModified, replicas = record.NodeIds,
                });
            }

            return await objects.GetAsync(ctx, bucket, key) switch
            {
                ServeOutcome.NotFound => NoSuchKey(key),
                ServeOutcome.NoReplica => Unavailable(),
                _ => Results.Empty,
            };
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

        v1.MapDelete("/buckets/{bucket}/objects/{**key}", (string bucket, string key, HttpContext ctx, GatewayMetadataStore store, ObjectService objects) =>
        {
            if (!store.BucketExists(bucket))
            {
                return NoSuchBucket(bucket);
            }

            objects.Delete(ctx, bucket, key); // idempotent, as in S3
            return Results.NoContent();
        });
    }

    /// <summary>
    /// <c>?history</c> (#204): the object's operations, oldest first, paged. Works for deleted objects,
    /// and an unknown bucket or key is an empty list, not a 404, so the API can't probe for existence.
    /// </summary>
    private static IResult History(string bucket, string key, IQueryCollection q, HistoryStore history)
    {
        var max = DefaultLimit;
        if (q["limit"].FirstOrDefault() is { } limit && (!int.TryParse(limit, out max) || max is < 1 or > MaxLimit))
        {
            return Error(400, "InvalidArgument", $"limit must be an integer from 1 to {MaxLimit}.");
        }

        DateTimeOffset? since = null, until = null;
        if (q["since"].FirstOrDefault() is { } s)
        {
            if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var v))
                return Error(400, "InvalidArgument", "since must be an ISO 8601 date-time.");
            since = v;
        }

        if (q["until"].FirstOrDefault() is { } u)
        {
            if (!DateTimeOffset.TryParse(u, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var v))
                return Error(400, "InvalidArgument", "until must be an ISO 8601 date-time.");
            until = v;
        }

        string[]? operations = null;
        if (q["operation"].FirstOrDefault() is { Length: > 0 } ops)
        {
            operations = ops.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (operations.FirstOrDefault(o => !HistoryOperation.All.Contains(o)) is { } bad)
                return Error(400, "InvalidArgument", $"operation '{bad}' is not one of insert, update, view, delete.");
        }

        long after = 0;
        if (q["cursor"].FirstOrDefault() is { } cursor && (!TryDecodeCursor(cursor, out var decoded) || !long.TryParse(decoded, out after)))
        {
            return Error(400, "InvalidArgument", "cursor is not one this API returned.");
        }

        var rows = history.Query(bucket, key, since, until, operations, after, max + 1);
        var page = rows.Take(max).ToList();
        return Results.Ok(new
        {
            entries = page.Select(e => new
            {
                id = e.Id, timestamp = e.Timestamp, operation = e.Operation, api = e.Api, size = e.Size, etag = e.Etag, cid = e.Cid,
                clientIp = e.ClientIp, accessKeyId = e.AccessKeyId, tenantId = e.TenantId, userAgent = e.UserAgent,
                requestId = e.RequestId, outcome = e.Outcome, status = e.Status,
            }),
            nextCursor = rows.Count > max ? EncodeCursor(page[^1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
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
