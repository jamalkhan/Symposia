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

        // Only the caller's tenant's buckets: listing never reveals another tenant's names (#3 FR1.3).
        v1.MapGet("/buckets", (HttpContext ctx, GatewayMetadataStore store) =>
            Results.Ok(new { buckets = store.ListBuckets(ctx.Items["TenantId"] as string ?? "").Select(b => new { name = b.Name, createdAt = b.CreatedAt }) }));

        v1.MapPut("/buckets/{bucket}", (string bucket, HttpContext ctx, GatewayMetadataStore store) =>
        {
            if (!BucketNames.IsValid(bucket))
            {
                return Error(400, "InvalidBucketName", BucketNames.Rule);
            }

            store.CreateBucket(bucket, ctx.Items["TenantId"] as string ?? ""); // idempotent, as in S3; the creator owns it
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
                    // #7 FR5: "quorum" until every target copy has confirmed, then "full".
                    replication = record.Replication == ReplicationState.Full ? "full" : "quorum",
                    targetCopies = record.TargetCopies, regions = record.Regions,
                    // #8: soft placement rules bent for this object, and its storage scheme and shards.
                    placementGaps = record.PlacementGaps.Select(g => new { rule = g.Rule, detail = g.Detail }),
                    scheme = record.Scheme,
                    shards = record.IsErasureCoded ? store.Shards(bucket, key).Select(sh => new { index = sh.Index, cid = sh.Cid, node = sh.NodeId }) : null,
                    pendingReplicas = store.PendingReplicas(record.Cid).Select(p => new { node = p.NodeId, stage = p.Stage, extra = p.Extra, done = p.Done }),
                });
            }

            return await objects.GetAsync(ctx, bucket, key) switch
            {
                ServeOutcome.NotFound => NoSuchKey(key),
                ServeOutcome.NoReplica => Unavailable(),
                ServeOutcome.Lost => Error(503, "ObjectDataLost", "Every replica of this object reports it missing; the data is lost, not temporarily unavailable."),
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

        // Region assignment (#7 FR1/FR3, #8 FR1.1): where the bucket's new objects are placed and which
        // regions their write quorum must include. Applies to writes from now on; existing objects keep theirs.
        // ?prefix= sets a folder rule instead (#8 FR1.1): object header, then longest folder rule, then bucket.
        v1.MapGet("/buckets/{bucket}/regions", (string bucket, string? prefix, HttpContext ctx, GatewayMetadataStore store) =>
            !OwnsBucket(ctx, store, bucket) ? NoSuchBucket(bucket)
            : prefix is null
                ? Results.Ok(new { regions = store.BucketRegions(bucket), folders = store.PrefixRegions(bucket).Select(r => new { prefix = r.Prefix, regions = r.Regions }) })
                : Results.Ok(new { prefix, regions = store.PrefixRegions(bucket).FirstOrDefault(r => r.Prefix == prefix).Regions ?? [] }));

        v1.MapPut("/buckets/{bucket}/regions", (string bucket, string? prefix, RegionAssignment body, HttpContext ctx, GatewayMetadataStore store,
            Microsoft.Extensions.Options.IOptions<GatewayOptions> options) =>
        {
            if (!OwnsBucket(ctx, store, bucket))
            {
                return NoSuchBucket(bucket);
            }

            var regions = (body.Regions ?? []).Select(r => r.Trim().ToLowerInvariant()).Distinct().ToList();
            if (regions.FirstOrDefault(r => !RegionCode().IsMatch(r)) is { } bad)
            {
                return Error(400, "InvalidArgument", $"'{bad}' is not a region code (lowercase letters, digits and dashes, up to 32).");
            }

            if (regions.Count > 0 && options.Value.Redundancy.Mode != RedundancyMode.Network)
            {
                // Bootstrap clusters don't place by region; accepting the setting would promise a constraint nobody enforces.
                return Error(409, "RegionsNotEnforced", "This gateway runs in bootstrap redundancy mode and does not place by region.");
            }

            if (prefix is { Length: > 0 })
            {
                store.SetPrefixRegions(bucket, prefix, regions);
                return Results.Ok(new { prefix, regions });
            }

            store.SetBucketRegions(bucket, regions);
            return Results.Ok(new { regions });
        });

        // Erasure coding per bucket (#8 FR5.1): "rs:k+m" stores new objects as shards; "replica" (default) as copies.
        v1.MapPut("/buckets/{bucket}/scheme", (string bucket, SchemeChoice body, HttpContext ctx, GatewayMetadataStore store) =>
        {
            if (!OwnsBucket(ctx, store, bucket))
            {
                return NoSuchBucket(bucket);
            }

            if (body.Scheme is not ("replica" or null) && !Domain.Erasure.ErasureScheme.TryParse(body.Scheme, out _))
            {
                return Error(400, "InvalidArgument", "scheme must be \"replica\" or \"rs:k+m\" with 1 ≤ k, 1 ≤ m, k + m ≤ 32.");
            }

            store.SetBucketScheme(bucket, body.Scheme is "replica" ? null : body.Scheme);
            return Results.Ok(new { scheme = body.Scheme ?? "replica" });
        });

        // Lifecycle and retention (#16 FR1–FR3).
        v1.MapPut("/buckets/{bucket}/lifecycle", (string bucket, BucketLifecycle body, HttpContext ctx, GatewayMetadataStore store) =>
        {
            if (!OwnsBucket(ctx, store, bucket))
            {
                return NoSuchBucket(bucket);
            }

            if (body.RecoveryDays is < 1 or > 365 || body.MultipartTtlHours is < 1 or > 720 || body.MaxVersions is < 1
                || body.MaxVersionAgeDays is < 1 || body.MinRetentionDays is < 0)
            {
                return Error(400, "InvalidArgument", "recoveryDays 1–365, multipartTtlHours 1–720, maxVersions ≥ 1, maxVersionAgeDays ≥ 1, minRetentionDays ≥ 0.");
            }

            store.SetLifecycle(bucket, body);
            return Results.Ok(body);
        });

        v1.MapGet("/buckets/{bucket}/lifecycle", (string bucket, HttpContext ctx, GatewayMetadataStore store) =>
            !OwnsBucket(ctx, store, bucket) ? NoSuchBucket(bucket) : Results.Ok(store.Lifecycle(bucket)));

        // ?retention sets an object's lock and legal hold; ?restore brings back a deleted object within its recovery window.
        v1.MapPost("/buckets/{bucket}/objects/{**key}", async (string bucket, string key, HttpContext ctx, GatewayMetadataStore store) =>
        {
            // The body is optional (?restore has none), so it's read by hand rather than bound.
            var body = ctx.Request.ContentLength > 0
                ? await ctx.Request.ReadFromJsonAsync<RetentionRequest>(ctx.RequestAborted)
                : null;
            if (!OwnsBucket(ctx, store, bucket))
            {
                return NoSuchBucket(bucket);
            }

            if (ctx.Request.Query.ContainsKey("restore"))
            {
                return store.RestoreLatestVersion(bucket, key) is { } restored ? Results.Ok(Describe(restored))
                    : Error(404, "NoSuchVersion", "No recoverable version of this key (the key is live, or its versions are past recovery).");
            }

            if (ctx.Request.Query.ContainsKey("retention"))
            {
                return store.SetRetention(bucket, key, body?.LockedUntil, body?.LegalHold ?? false)
                    ? Results.Ok(new { lockedUntil = body?.LockedUntil, legalHold = body?.LegalHold ?? false })
                    : NoSuchKey(key);
            }

            return Error(400, "InvalidArgument", "POST on an object takes ?restore or ?retention.");
        });

        // What's waiting for physical deletion and which copies can't be re-placed (#16 FR10), for the caller's tenant only.
        v1.MapGet("/gc", (HttpContext ctx, GatewayMetadataStore store, Microsoft.Extensions.Options.IOptions<GatewayOptions> options) =>
        {
            var tenant = ctx.Items["TenantId"] as string ?? "";
            return Results.Ok(GcReport(store, options.Value, tenant));
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

    /// <summary>Whether the caller's tenant owns <paramref name="bucket"/>; a stranger's bucket reads as absent.</summary>
    private static bool OwnsBucket(HttpContext ctx, GatewayMetadataStore store, string bucket) =>
        store.BucketExists(bucket) && store.BucketTenant(bucket) == ctx.Items["TenantId"] as string;

    private sealed record RegionAssignment(string[]? Regions);

    private sealed record SchemeChoice(string? Scheme);

    private sealed record RetentionRequest(DateTimeOffset? LockedUntil, bool? LegalHold);

    /// <summary>Pending physical deletions per bucket with an estimated purge time, and open placement warnings (#16 FR10).</summary>
    internal static object GcReport(GatewayMetadataStore store, GatewayOptions options, string? tenant) => new
    {
        pendingDeletion = store.PendingDeletions(tenant).Select(p => new
        {
            bucket = p.Bucket,
            objects = p.Pending,
            queued = p.Queued,
            // The oldest soft-deleted version is purgeable at the end of its window; queued ones within 72 h (#16 FR2.2).
            estimatedPurgeBy = p.Queued > 0 ? DateTimeOffset.UtcNow.AddHours(72)
                : p.OldestDeletedAt?.AddDays(store.Lifecycle(p.Bucket).RecoveryDays ?? options.Gc.RecoveryDays),
        }),
        placementWarnings = store.PlacementWarnings(tenant).Select(w => new { cid = w.Cid, bucket = w.Bucket, key = w.Key, reason = w.Reason, since = w.Since }),
    };

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial System.Text.RegularExpressions.Regex RegionCode();

    /// <summary>A JSON error, <c>{"error":{"code","message","requestId"}}</c>, with S3's codes and statuses.</summary>
    public static IResult Error(int status, string code, string message) => new ErrorResult(status, code, message);

    private static IResult NoSuchBucket(string bucket) => Error(404, "NoSuchBucket", $"The bucket '{bucket}' does not exist.");

    private static IResult NoSuchKey(string key) => Error(404, "NoSuchKey", $"The key '{key}' does not exist.");

    private static IResult Unavailable() => S3Xml.WithRetryAfter(Error(503, "ServiceUnavailable", "No storage node could serve the request; try again."));

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
