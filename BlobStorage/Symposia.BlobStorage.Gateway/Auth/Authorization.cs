using System.Text;
using Symposia.BlobStorage.Domain.Credentials;
using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.Auth;

/// <summary>
/// Request-time authorization (#3 FR4): after SigV4 authenticated a credential, the request is classified
/// (read or write; bucket, key, list prefix) and <see cref="AccessEvaluator"/> decides from cached state,
/// with no call off-box. A refusal is a 403 with its specific code; every decision is audited.
/// </summary>
public static class Authorization
{
    /// <summary>Management paths proxied to the Authority: identity checks only; the Authority does the rest.</summary>
    public static bool IsManagementPath(string path) =>
        path.StartsWith("/v1/credentials", StringComparison.Ordinal) || path.StartsWith("/v1/policies", StringComparison.Ordinal)
        || path.StartsWith("/v1/audit", StringComparison.Ordinal) || path == "/v1/gc" || path == "/v1/buckets";

    /// <summary>The bucket, key and kind of a data-plane request, from its path, method and query.</summary>
    public static (string? Bucket, string? Key, bool IsWrite, string? ListPrefix) Classify(HttpRequest request)
    {
        var path = request.Path.Value ?? "/";
        var isWrite = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
        string? bucket = null, key = null;
        if (path.StartsWith("/v1/buckets/", StringComparison.Ordinal))
        {
            var rest = path["/v1/buckets/".Length..];
            var slash = rest.IndexOf('/');
            bucket = slash < 0 ? rest : rest[..slash];
            if (slash >= 0 && rest[(slash + 1)..].StartsWith("objects/", StringComparison.Ordinal))
            {
                key = Uri.UnescapeDataString(rest[(slash + 1 + "objects/".Length)..]);
            }
        }
        else if (!path.StartsWith("/v1", StringComparison.Ordinal) && path.Length > 1)
        {
            var trimmed = path[1..];
            var slash = trimmed.IndexOf('/');
            bucket = slash < 0 ? trimmed : trimmed[..slash];
            key = slash < 0 || slash == trimmed.Length - 1 ? null : Uri.UnescapeDataString(trimmed[(slash + 1)..]);
        }

        return (bucket is { Length: > 0 } ? Uri.UnescapeDataString(bucket) : null, key, isWrite, request.Query["prefix"].FirstOrDefault());
    }

    /// <summary>The decision for the request, plus the copy source's read when it is a CopyObject.</summary>
    public static AccessDecision Decide(HttpContext ctx, CredentialGrant grant, CredentialStore cache, GatewayMetadataStore store, string gatewayRegion,
        string? signedRegion, DateTimeOffset now)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var regions = new[] { gatewayRegion, signedRegion }.OfType<string>().Where(r => r.Length > 0).Distinct().ToList();
        var tenant = cache.TenantStatus(grant.TenantId);
        if (IsManagementPath(path) || path.StartsWith("/admin/", StringComparison.Ordinal))
        {
            return AccessEvaluator.Evaluate(grant, tenant, new AccessRequest(false, null, null, null, null, regions), [], now);
        }

        var (bucket, key, isWrite, prefix) = Classify(ctx.Request);
        var decision = AccessEvaluator.Evaluate(grant, tenant,
            new AccessRequest(isWrite, bucket, key, bucket is null ? null : store.BucketTenant(bucket), prefix, regions), cache.Rules, now);
        if (decision == AccessDecision.Allowed && ctx.Request.Headers["x-amz-copy-source"].FirstOrDefault() is { } source)
        {
            var sourcePath = Uri.UnescapeDataString(source.TrimStart('/'));
            var slash = sourcePath.IndexOf('/');
            if (slash > 0)
            {
                var sourceBucket = sourcePath[..slash];
                decision = AccessEvaluator.Evaluate(grant, tenant,
                    new AccessRequest(false, sourceBucket, sourcePath[(slash + 1)..], store.BucketTenant(sourceBucket), null, regions), cache.Rules, now);
            }
        }

        return decision;
    }

    /// <summary>The refusal in the API's own error format: S3 XML, or the /v1 JSON error.</summary>
    public static IResult Refusal(HttpContext ctx, AccessDecision decision)
    {
        var message = decision switch
        {
            AccessDecision.CredentialRevoked => "The credential has been revoked.",
            AccessDecision.TenantSuspended => "The tenant is suspended.",
            AccessDecision.CredentialExpired => "The credential has expired.",
            AccessDecision.RegionLockViolation => "The credential is not valid in this region.",
            AccessDecision.CrossTenantAccessDenied => "Access denied.", // never says whether the resource exists
            AccessDecision.InsufficientCredentialScope => "The credential's access level or scope does not allow this request.",
            _ => "Access denied by bucket policy or object ACL.",
        };
        return ctx.Request.Path.StartsWithSegments(Api.BlobApiEndpoints.Prefix)
            ? Api.BlobApiEndpoints.Error(403, decision.ToString(), message)
            : S3.S3Xml.Error(403, decision.ToString(), message);
    }

    // ── Management proxy (#3: tenant credential management through the gateway, authenticated by SigV4) ──

    public static void MapManagement(IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup(Api.BlobApiEndpoints.Prefix);
        v1.MapGet("/credentials", Proxy("internal/credentials"));
        v1.MapPost("/credentials", Proxy("internal/credentials"));
        v1.MapPost("/credentials/{id}/revoke", (HttpContext ctx, string id) => Proxy($"internal/credentials/{Uri.EscapeDataString(id)}/revoke")(ctx));
        v1.MapPost("/credentials/{id}/rotate", (HttpContext ctx, string id) => Proxy($"internal/credentials/{Uri.EscapeDataString(id)}/rotate")(ctx));
        v1.MapPut("/policies/{bucket}", (HttpContext ctx, string bucket) => Proxy($"internal/policies/{Uri.EscapeDataString(bucket)}{ctx.Request.QueryString}")(ctx));
        // Typed as Func<…, Task<IResult>>: a bare (HttpContext) lambda would bind as a RequestDelegate and drop the result.
        v1.MapGet("/audit", (Func<HttpContext, Task<IResult>>)(ctx => Proxy($"internal/audit{ctx.Request.QueryString}")(ctx)));
        v1.MapGet("/audit/verify", Proxy("internal/audit/verify"));
    }

    private static Func<HttpContext, Task<IResult>> Proxy(string target) => async ctx =>
    {
        var options = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.CredentialAuthority;
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            return Api.BlobApiEndpoints.Error(501, "NotImplemented", "Credential management needs a Credential Authority; this gateway has none configured.");
        }

        var credentialId = ctx.Items[Health.RequestLog.CredentialItem] as string ?? "";
        if (credentialId.StartsWith("static:", StringComparison.Ordinal))
        {
            return Api.BlobApiEndpoints.Error(403, "InsufficientCredentialScope", "Configured credentials can't manage Authority credentials.");
        }

        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
        request.Headers.Add("X-Acting-Credential", credentialId);
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            using var reader = new StreamReader(ctx.Request.Body);
            request.Content = new StringContent(await reader.ReadToEndAsync(ctx.RequestAborted), Encoding.UTF8, "application/json");
        }

        using var response = await ctx.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(CredentialSync.HttpClientName)
            .SendAsync(request, ctx.RequestAborted);
        var body = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
        if (response.IsSuccessStatusCode && HttpMethods.IsPost(ctx.Request.Method))
        {
            // Make the change visible here at once rather than at the next push or poll.
            await ctx.RequestServices.GetRequiredService<CredentialSync>().PullAsync(ctx.RequestAborted);
        }

        return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
    };
}
