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

    /// <summary>Longest User-Agent kept, so one client can't inflate every row it causes.</summary>
    public const int MaxUserAgent = 256;

    public static IApplicationBuilder UseHistoryCapture(this IApplicationBuilder app, IReadOnlyList<IPNetwork> trustedProxies) => app.Use(async (ctx, next) =>
    {
        var failed = false;
        try
        {
            await next();
        }
        catch
        {
            failed = true; // the status still reads 200 here; the server turns this into a 500 afterwards
            throw;
        }
        finally
        {
            if (ctx.Items[Key] is List<PendingOperation> ops)
            {
                var recorder = ctx.RequestServices.GetRequiredService<HistoryRecorder>();
                var sigv4 = ctx.Items[nameof(SigV4Context)] as SigV4Context;
                var ip = ClientAddress.Resolve(ctx.Connection.RemoteIpAddress, ctx.Request.Headers["X-Forwarded-For"].ToString(), trustedProxies)?.ToString();
                var status = failed && !ctx.Response.HasStarted ? StatusCodes.Status500InternalServerError : ctx.Response.StatusCode;
                var userAgent = ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, MaxUserAgent)] : null;
                var now = recorder.Now;
                foreach (var op in ops)
                {
                    recorder.Record(new HistoryEntry(0, now, op.Operation, op.Bucket, op.Key, op.Api, op.Size, op.Etag, op.Cid,
                        ip, sigv4?.AccessKeyId, sigv4?.TenantId, userAgent, RequestIds.Of(ctx), failed ? "Error" : op.Outcome ?? OutcomeOf(status), status));
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
