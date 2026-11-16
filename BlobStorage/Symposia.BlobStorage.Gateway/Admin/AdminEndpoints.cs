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

        // #8: penalty stages (until the on-chain penalty system feeds them), repair SLAs, fault-domain audit.
        group.MapPost("/nodes/penalty", SetPenalty).AddEndpointFilter(AdminAuthFilter);
        group.MapGet("/repair/sla", RepairSla).AddEndpointFilter(AdminAuthFilter);
        group.MapGet("/placement/ip-violations", IpViolations).AddEndpointFilter(AdminAuthFilter);
        // #12: the routing cache's state, and resolving a holding dispute against a node's on-chain epoch root (FR3.4).
        group.MapGet("/routing", (Routing.RoutingCache cache) => Results.Ok(new
        {
            warm = cache.IsWarm,
            startedAt = cache.StartedAt,
            warmSince = cache.WarmSince,
            nodes = cache.Nodes.Select(n => new { node = n, cids = cache.CountFor(n) }),
        })).AddEndpointFilter(AdminAuthFilter);
        group.MapPost("/routing/disputes", ResolveDispute).AddEndpointFilter(AdminAuthFilter);

        // #15 FR8: the billing system raises or removes a tenant's platform-imposed limit when payment is added.
        group.MapPut("/tenants/{tenant}/quota", Api.QuotaEndpoints.SetPlatformLimit).AddEndpointFilter(AdminAuthFilter);
        group.MapGet("/gc", (Metadata.GatewayMetadataStore store, IOptions<GatewayOptions> options, GC.SafetyInvariantGate gate) => Results.Ok(new
        {
            report = Api.BlobApiEndpoints.GcReport(store, options.Value, null),
            safetyRefusals = gate.Refusals, // anything but 0 means an evaluator queued protected data (#16 AC10)
        })).AddEndpointFilter(AdminAuthFilter);
    }

    private static async Task<IResult> ResolveDispute(DisputeRequest request, Nodes.INodeRegistry nodes, Routing.EpochRootDisputes disputes,
        CancellationToken ct)
    {
        if (!Domain.Cid.TryParse(request.Cid, out _) || nodes.All.FirstOrDefault(n => string.Equals(n.Id, request.Node, StringComparison.OrdinalIgnoreCase)) is not { } node)
            return Results.BadRequest(new { error = "cid must be a CID and node a known node ID" });

        var outcome = await disputes.ResolveAsync(node, request.Cid, ct);
        return Results.Ok(new { node = node.Id, cid = request.Cid, outcome = outcome.ToString() });
    }

    private sealed record DisputeRequest(string Node, string Cid);

    private static IResult SetPenalty(PenaltyRequest request, Penalties.ManualPenaltySource penalties)
    {
        if (string.IsNullOrWhiteSpace(request.Node) || request.Stage is < 0 or > 4)
            return Results.BadRequest(new { error = "node is required and stage must be 0–4" });

        penalties.Set(request.Node, request.Stage);
        return Results.Ok(new { node = request.Node, stage = request.Stage });
    }

    /// <summary>Per tier over the window: completed, completed within the tier's SLA, and open past it (#8 FR4.5, AC5).</summary>
    private static IResult RepairSla(Metadata.GatewayMetadataStore store, Repair.RepairQueue queue, int? hours)
    {
        var since = DateTimeOffset.UtcNow - TimeSpan.FromHours(hours ?? 24);
        var waiting = queue.PendingByTier;
        return Results.Ok(new
        {
            since,
            tiers = store.RepairSlaReport(since, Repair.RepairQueue.Slas).Select(t => new
            {
                priority = $"P{t.Priority}",
                slaSeconds = Repair.RepairQueue.Slas[t.Priority].TotalSeconds,
                completed = t.Completed,
                withinSla = t.WithinSla,
                compliancePct = t.Completed == 0 ? 100.0 : Math.Round(100.0 * t.WithinSla / t.Completed, 2),
                openPastSla = t.OpenOverdue,
                waiting = waiting[t.Priority],
            }),
        });
    }

    /// <summary>Every object with two replicas on nodes sharing a public IP (#8 AC2 audit: must be empty).</summary>
    private static IResult IpViolations(Metadata.GatewayMetadataStore store, INodeRegistry nodes)
    {
        var ipOf = nodes.All.Where(n => n.PublicIp.Length > 0).ToDictionary(n => n.Id, n => n.PublicIp, StringComparer.OrdinalIgnoreCase);
        var violations = new List<object>();
        (string Bucket, string Key)? cursor = null;
        while (true)
        {
            var page = store.ListAllObjectsPaged(cursor, 500);
            foreach (var record in page)
            {
                foreach (var shared in record.NodeIds.Where(ipOf.ContainsKey).GroupBy(id => ipOf[id]).Where(g => g.Count() > 1))
                {
                    violations.Add(new { bucket = record.Bucket, key = record.Key, ip = shared.Key, nodes = shared.ToList() });
                }
            }

            if (page.Count < 500) break;
            cursor = (page[^1].Bucket, page[^1].Key);
        }

        return Results.Ok(new { violations, count = violations.Count });
    }

    private sealed record PenaltyRequest(string Node, int Stage);

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
            region = n.Region,
            publicIp = n.PublicIp,
            penaltyStage = n.PenaltyStage,
            challengeFailures = n.ChallengeFailures,
            // What this gateway's challenge record argues for; the penalty system decides (#8 AC9).
            recommendedPenaltyStage = Penalties.PenaltyAssessment.RecommendedStage(n.ChallengeFailures),
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
