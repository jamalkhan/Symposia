namespace Symposia.BlobStorage.Gateway.Api;

/// <summary>
/// A request ID on every response (#203; #204 records it in operation history):
/// <c>x-symposia-request-id</c>, mirrored as <c>x-amz-request-id</c> on the S3 surface.
/// </summary>
public static class RequestIds
{
    public const string Header = "x-symposia-request-id";
    private const string Key = "RequestId";

    public static string Of(HttpContext ctx) => ctx.Items[Key] as string ?? "";

    public static IApplicationBuilder UseRequestIds(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        ctx.Items[Key] = id;
        ctx.Response.Headers[Header] = id;
        if (!ctx.Request.Path.StartsWithSegments(BlobApiEndpoints.Prefix))
        {
            ctx.Response.Headers["x-amz-request-id"] = id;
        }

        await next();
    });
}
