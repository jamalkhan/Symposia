using System.Text.Json.Serialization;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Quota;

namespace Symposia.BlobStorage.Gateway.Api;

/// <summary>
/// The quota API (#15 FR6), under <c>/v1</c> so it can't collide with S3 bucket names. Field names are the snake_case
/// shape Requirements/BlobStorage/quota-management.md defines. Usage is what's committed; <c>available_bytes</c> also
/// subtracts in-flight reservations. A null limit is unlimited, and then <c>available_bytes</c> and
/// <c>utilization_pct</c> are null too (QA open question 4).
/// </summary>
internal static class QuotaEndpoints
{
    internal static void Map(RouteGroupBuilder v1)
    {
        v1.MapGet("/account/quota", (HttpContext ctx, QuotaGate quota, GatewayMetadataStore store) =>
            Results.Ok(AccountView(Tenant(ctx), quota, store)));

        // A self-limit above the platform's is accepted; the effective limit is the lower of the two (QA-30).
        v1.MapPut("/account/quota", (SelfLimitRequest body, HttpContext ctx, QuotaGate quota, GatewayMetadataStore store) =>
        {
            if (body.TenantSelfLimitBytes is not > 0)
            {
                return BlobApiEndpoints.Error(400, "InvalidArgument", "tenant_self_limit_bytes must be a positive integer.");
            }

            var tenant = Tenant(ctx);
            store.SetSelfLimit(tenant, quota.DefaultsFor(tenant), body.TenantSelfLimitBytes);
            return Results.Ok(AccountView(tenant, quota, store));
        });

        // Removing the self-limit never removes the platform's (FR9).
        v1.MapDelete("/account/quota/self-limit", (HttpContext ctx, QuotaGate quota, GatewayMetadataStore store) =>
        {
            var tenant = Tenant(ctx);
            store.SetSelfLimit(tenant, quota.DefaultsFor(tenant), null);
            return Results.Ok(AccountView(tenant, quota, store));
        });

        v1.MapGet("/buckets/{bucket}/quota", (string bucket, HttpContext ctx, GatewayMetadataStore store) =>
            !Owns(ctx, store, bucket) ? NoSuchBucket(bucket) : Results.Ok(BucketView(bucket, store)));

        v1.MapPut("/buckets/{bucket}/quota", (string bucket, BucketQuotaRequest body, HttpContext ctx, GatewayMetadataStore store) =>
        {
            if (!Owns(ctx, store, bucket))
            {
                return NoSuchBucket(bucket);
            }

            if (body.LimitBytes is null && body.MaxObjectCount is null || body.LimitBytes is <= 0 || body.MaxObjectCount is <= 0)
            {
                return BlobApiEndpoints.Error(400, "InvalidArgument", "Set limit_bytes and/or max_object_count, each a positive integer.");
            }

            store.SetBucketQuota(bucket, body.LimitBytes, body.MaxObjectCount);
            return Results.Ok(BucketView(bucket, store));
        });

        // The account quota still applies afterwards (FR9).
        v1.MapDelete("/buckets/{bucket}/quota", (string bucket, HttpContext ctx, GatewayMetadataStore store) =>
        {
            if (!Owns(ctx, store, bucket))
            {
                return NoSuchBucket(bucket);
            }

            store.SetBucketQuota(bucket, null, null);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// What the billing system calls when a tenant's payment credentials or contract change (#15 FR8, AC12): sets the
    /// platform-imposed limit (null = none) and takes the account off the free tier. Admin-only, under <c>/admin</c>.
    /// </summary>
    internal static IResult SetPlatformLimit(string tenant, PlatformLimitRequest body, QuotaGate quota, GatewayMetadataStore store)
    {
        if (body.PlatformImposedLimitBytes is <= 0)
        {
            return Results.BadRequest(new { error = "platform_imposed_limit_bytes must be positive, or null for no platform limit" });
        }

        store.SetPlatformLimit(tenant, quota.DefaultsFor(tenant), body.PlatformImposedLimitBytes, freeTier: body.FreeTier ?? false);
        return Results.Ok(AccountView(tenant, quota, store));
    }

    internal static object AccountView(string tenant, QuotaGate quota, GatewayMetadataStore store)
    {
        var account = quota.Account(tenant);
        var usage = store.AccountUsage(tenant);
        var effective = quota.EffectiveLimit(account);
        return new
        {
            limit_bytes = effective,
            used_bytes = usage.UsedBytes,
            reserved_bytes = usage.ReservedBytes,
            available_bytes = effective is { } l ? Math.Max(0, l - usage.UsedBytes - usage.ReservedBytes) : (long?)null,
            utilization_pct = effective is { } e ? QuotaGate.Utilization(usage.UsedBytes, e) : (double?)null,
            platform_imposed_limit_bytes = quota.PlatformLimit(account),
            tenant_self_limit_bytes = account.SelfLimitBytes,
            effective_limit_bytes = effective,
            egress_limit_bytes_per_month = account.EgressLimitBytesPerMonth,
            free_tier = account.FreeTier,
        };
    }

    private static object BucketView(string bucket, GatewayMetadataStore store)
    {
        var quota = store.GetBucketQuota(bucket);
        var usage = store.BucketUsage(bucket);
        var limit = quota?.LimitBytes;
        return new
        {
            limit_bytes = limit,
            used_bytes = usage.UsedBytes,
            reserved_bytes = usage.ReservedBytes,
            available_bytes = limit is { } l ? Math.Max(0, l - usage.UsedBytes - usage.ReservedBytes) : (long?)null,
            utilization_pct = limit is { } u ? QuotaGate.Utilization(usage.UsedBytes, u) : (double?)null,
            max_object_count = quota?.MaxObjectCount,
            object_count = usage.Objects,
        };
    }

    private static string Tenant(HttpContext ctx) => ctx.Items["TenantId"] as string ?? "";

    // A stranger's bucket reads as absent (#3 FR1.3).
    private static bool Owns(HttpContext ctx, GatewayMetadataStore store, string bucket) =>
        store.BucketExists(bucket) && store.BucketTenant(bucket) == Tenant(ctx);

    private static IResult NoSuchBucket(string bucket) => BlobApiEndpoints.Error(404, "NoSuchBucket", $"The specified bucket does not exist: {bucket}");

    internal sealed record SelfLimitRequest([property: JsonPropertyName("tenant_self_limit_bytes")] long? TenantSelfLimitBytes);

    internal sealed record BucketQuotaRequest(
        [property: JsonPropertyName("limit_bytes")] long? LimitBytes,
        [property: JsonPropertyName("max_object_count")] long? MaxObjectCount);

    internal sealed record PlatformLimitRequest(
        [property: JsonPropertyName("platform_imposed_limit_bytes")] long? PlatformImposedLimitBytes,
        [property: JsonPropertyName("free_tier")] bool? FreeTier);
}
