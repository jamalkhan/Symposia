using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Prometheus;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Health;

/// <summary>
/// Liveness, readiness and metrics for load balancers, orchestrators and monitoring (#1 FR-23–27), per
/// Requirements/BlobStorage/health-check-endpoints.md. <c>/health</c> and <c>/ready</c> are unauthenticated
/// and read only in-memory state, so they answer within their 100 ms / 500 ms budgets whatever the nodes
/// or the index are doing. The older <c>/healthz/*</c> routes stay for one release as aliases.
/// </summary>
public static class HealthEndpoints
{
    public const string HealthPath = "/health";
    public const string ReadyPath = "/ready";
    public const string MetricsPath = "/metrics";

    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    internal static readonly string Version =
        typeof(HealthEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Paths that skip SigV4 and the request log's tenant requirement.</summary>
    public static bool IsHealthPath(string path) =>
        path is HealthPath or ReadyPath or MetricsPath || path.StartsWith("/healthz/", StringComparison.Ordinal);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(HealthPath, Health);
        app.MapGet("/healthz/live", Health);
        app.MapGet(ReadyPath, Ready);
        app.MapGet("/healthz/ready", Ready);
        app.MapGet(MetricsPath, Metrics);
    }

    private static IResult Health() => Results.Ok(new
    {
        status = "ok",
        version = Version,
        uptime_seconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
    });

    /// <summary>The not-ready reason, or null when ready. Checked in the order the spec lists them.</summary>
    internal static (string Reason, int RetryAfterSeconds)? NotReadyReason(
        INodeRegistry nodes, MetadataIndexMonitor index, ShutdownState shutdown, int writeQuorum)
    {
        if (shutdown.IsDraining)
        {
            return ("draining", 30);
        }

        if (!nodes.CacheLoaded)
        {
            return ("node_cache_not_loaded", 5);
        }

        if (!index.IsConnected)
        {
            return ("metadata_index_not_connected", 5);
        }

        if (nodes.Writable().Count < writeQuorum)
        {
            return ("insufficient_eligible_nodes", 10);
        }

        return null;
    }

    private static IResult Ready(INodeRegistry nodes, MetadataIndexMonitor index, ShutdownState shutdown, Quorum.IWriteQuorumRequirement quorum)
    {
        if (NotReadyReason(nodes, index, shutdown, quorum.MinimumEligibleNodes) is { } notReady)
        {
            return Results.Json(new { status = "not_ready", reason = notReady.Reason, retry_after_seconds = notReady.RetryAfterSeconds },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new
        {
            status = "ready",
            node_cache_size = nodes.All.Count,
            metadata_index_connected = true,
            min_eligible_nodes_available = true,
        });
    }

    private static async Task Metrics(HttpContext ctx, IOptions<GatewayOptions> options, GatewayMetrics metrics,
        INodeRegistry nodes, MetadataIndexMonitor index)
    {
        if (!BearerMatches(ctx.Request, options.Value.MetricsBearerToken))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        // Gauges of current state are read at scrape time; counters accumulate as requests run.
        metrics.NodeCacheSize.Set(nodes.All.Count);
        metrics.EligibleNodes.Set(nodes.Writable().Count);
        metrics.MetadataIndexLag.Set(index.LagSeconds);
        foreach (var node in nodes.All)
        {
            metrics.NodeSelectionScore.WithLabels(node.Id).Set(ReplicaScore(node));
        }

        ctx.Response.ContentType = PrometheusConstants.TextContentTypeWithVersionAndEncoding;
        await metrics.Registry.CollectAndExportAsTextAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    /// <summary>The read-routing preference shown in <c>gateway_node_selection_score</c>: 1 healthy, 0 not.</summary>
    private static double ReplicaScore(NodeConnection node) => node.IsHealthy ? 1 : 0;

    /// <summary>Constant-time comparison of <c>Authorization: Bearer …</c> against the configured token; empty token = refuse.</summary>
    internal static bool BearerMatches(HttpRequest request, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(header[scheme.Length..].Trim())),
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
