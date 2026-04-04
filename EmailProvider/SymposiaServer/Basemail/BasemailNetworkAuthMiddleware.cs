namespace NativeSmtpReceiver;

public sealed class BasemailNetworkAuthMiddleware
{
    private readonly RequestDelegate _next;

    public BasemailNetworkAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        BasemailNodeOptions options,
        BasemailRequestSignatureValidator validator)
    {
        if (!options.NetworkEnabled ||
            !options.RequireSignedRequests ||
            !context.Request.Path.StartsWithSegments("/network", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/network/status", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        context.Request.EnableBuffering();
        await using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        var body = buffer.ToArray();
        context.Request.Body.Position = 0;

        var result = validator.Validate(context.Request, body);
        if (!result.Succeeded)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = result.ErrorMessage ?? "Basemail signature validation failed."
            }, context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
