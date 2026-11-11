using System.Net;
using Symposia.BlobStorage.Gateway.Api;
using Symposia.BlobStorage.Gateway.Auth;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.History;

/// <summary>
/// Turns the operations handlers mark (<see cref="Mark"/>) into history entries once the response has
/// completed (issue #204), so each carries the final status, the caller's access key and client IP.
/// Runs after authentication: an unauthenticated request has no caller to attribute, so it records nothing.
/// </summary>
public static class HistoryCapture
{
    private const string Key = "HistoryOperations";

    /// <summary>Marks an operation on this request; the handler fills in size, ETag and CID as it learns them.</summary>
    public static PendingOperation Mark(HttpContext ctx, string operation, string bucket, string key, string api)
    {
        var op = new PendingOperation(operation, bucket, key, api);
        if (ctx.Items[Key] is not List<PendingOperation> list)
        {
            ctx.Items[Key] = list = [];
        }

        list.Add(op);
        return op;
    }

    public static string ApiOf(HttpContext ctx) => ctx.Request.Path.StartsWithSegments(BlobApiEndpoints.Prefix) ? "v1" : "s3";

    public static IApplicationBuilder UseHistoryCapture(this IApplicationBuilder app, IReadOnlyList<IPNetwork> trustedProxies) => app.Use(async (ctx, next) =>
    {
        try
        {
            await next();
        }
        finally
        {
            if (ctx.Items[Key] is List<PendingOperation> ops)
            {
                var recorder = ctx.RequestServices.GetRequiredService<HistoryRecorder>();
                var sigv4 = ctx.Items[nameof(SigV4Context)] as SigV4Context;
                var ip = ClientAddress.Resolve(ctx.Connection.RemoteIpAddress, ctx.Request.Headers["X-Forwarded-For"].ToString(), trustedProxies)?.ToString();
                var status = ctx.Response.StatusCode;
                foreach (var op in ops)
                {
                    recorder.Record(new HistoryEntry(0, DateTimeOffset.UtcNow, op.Operation, op.Bucket, op.Key, op.Api, op.Size, op.Etag, op.Cid,
                        ip, sigv4?.AccessKeyId, sigv4?.TenantId, ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
                        RequestIds.Of(ctx), op.Outcome ?? OutcomeOf(status), status));
                }
            }
        }
    });

    private static string OutcomeOf(int status) => status switch
    {
        >= 200 and < 300 => "Success",
        404 => "NotFound",
        401 or 403 => "AccessDenied",
        _ => "Error",
    };
}
