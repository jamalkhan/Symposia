using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Admin;

/// <summary>
/// Internal admin API for managing the live node registry.
/// All endpoints require the X-Admin-Secret header to match Gateway:AdminSecret in config.
/// Set AdminSecret to a non-empty value to enable these endpoints.
///
/// POST   /admin/nodes         — register a new storage node URL
/// DELETE /admin/nodes         — deregister a storage node URL (body: { "url": "..." })
/// GET    /admin/nodes         — list all registered nodes with health status
/// </summary>
internal static class AdminEndpoints
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin");

        group.MapGet("/nodes", GetNodes).AddEndpointFilter(AdminAuthFilter);
        group.MapPost("/nodes", AddNode).AddEndpointFilter(AdminAuthFilter);
        group.MapDelete("/nodes", RemoveNode).AddEndpointFilter(AdminAuthFilter);
    }

    private static IResult GetNodes(INodeRegistry nodes)
    {
        var list = nodes.All.Select(n => new
        {
            url = n.Url,
            healthy = n.IsHealthy,
            lastProbeTime = n.LastProbeTime == DateTimeOffset.MinValue ? (DateTimeOffset?)null : n.LastProbeTime,
            blobCount = n.LastProbe?.BlobCount,
            usedStorageBytes = n.LastProbe?.UsedStorageBytes,
            availableStorageBytes = n.LastProbe?.AvailableStorageBytes,
        }).ToList();

        return Results.Ok(new { nodes = list, count = list.Count });
    }

    private static IResult AddNode(NodeUrlRequest request, INodeRegistry nodes)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return Results.BadRequest(new { error = "url is required" });

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
            return Results.BadRequest(new { error = "url must be an absolute URI" });

        nodes.AddNode(request.Url.TrimEnd('/'));
        return Results.Ok(new { registered = request.Url });
    }

    // DELETE uses a query parameter since HTTP DELETE does not allow a body in minimal API.
    private static IResult RemoveNode([FromQuery] string? url, INodeRegistry nodes)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Results.BadRequest(new { error = "url query parameter is required" });

        var removed = nodes.RemoveNode(url.TrimEnd('/'));
        return removed
            ? Results.Ok(new { removed = url })
            : Results.NotFound(new { error = "Node not found", url });
    }

    // ── Auth filter ───────────────────────────────────────────────────────────

    private static async ValueTask<object?> AdminAuthFilter(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var options = ctx.HttpContext.RequestServices
            .GetRequiredService<IOptions<GatewayOptions>>().Value;

        if (string.IsNullOrEmpty(options.AdminSecret))
            return Results.StatusCode(503); // Admin endpoints not enabled.

        var header = ctx.HttpContext.Request.Headers["X-Admin-Secret"].FirstOrDefault();
        if (header != options.AdminSecret)
            return Results.StatusCode(401);

        return await next(ctx);
    }

    private sealed record NodeUrlRequest(string Url);
}
