using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Prometheus;
using Symposia.BlobStorage.StorageNode.Storage;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.StorageNode.Health;

/// <summary>
/// The storage node's liveness, readiness and metrics (#1 FR-23, FR-25, FR-26), per
/// Requirements/BlobStorage/health-check-endpoints.md. Readiness reads only cached state: the storage
/// path is probed in the background by <see cref="StorageWritableMonitor"/>, never inside the request.
/// </summary>
public static class NodeHealthEndpoints
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private static readonly string Version =
        typeof(NodeHealthEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", Health);
        app.MapGet("/healthz/live", Health);
        app.MapGet("/ready", Ready);
        app.MapGet("/healthz/ready", Ready);
        app.MapGet("/metrics", Metrics);
    }

    private static IResult Health(NodeIdentity identity) => Results.Ok(new
    {
        status = "ok",
        node_id = identity.NodeId,
        version = Version,
        uptime_seconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
    });

    /// <summary>The not-ready reason and retry hint, or null when ready, in the order the spec lists them.</summary>
    public static (string Reason, int RetryAfterSeconds)? NotReadyReason(
        NodeNetworkStatus network, StorageNodeOptions options, StorageWritableMonitor storage, NodeShutdownState shutdown)
    {
        if (shutdown.IsDraining) return ("draining", 30);
        if (!network.NetworkJoined) return ("network_not_joined", 10);
        if (!network.RegistrationConfirmed) return ("registration_not_confirmed", 30);
        if (options.RequireRegionVerification && !options.RegionVerified) return ("region_verification_pending", 30);
        if (!storage.IsWritable) return ("storage_not_writable", 10);
        return null;
    }

    private static IResult Ready(NodeIdentity identity, NodeNetworkStatus network, IOptions<StorageNodeOptions> options,
        StorageWritableMonitor storage, NodeShutdownState shutdown, StorageCapacityMonitor capacity, PeerTable peers)
    {
        var o = options.Value;
        if (NotReadyReason(network, o, storage, shutdown) is { } notReady)
        {
            return Results.Json(new { status = "not_ready", reason = notReady.Reason, retry_after_seconds = notReady.RetryAfterSeconds },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var current = capacity.Current;
        return Results.Ok(new
        {
            status = "ready",
            node_id = identity.NodeId,
            region = o.Region,
            tier = o.Tier,
            penalty_stage = 0, // assigned on chain by the penalty system; the node itself doesn't track it yet
            storage_available_bytes = current.Available,
            storage_used_bytes = current.Used,
            peer_count = peers.Recognized().Count,
            region_verified = o.RegionVerified,
        });
    }

    private static async Task Metrics(HttpContext ctx, IOptions<StorageNodeOptions> options, NodeMetrics metrics,
        StorageCapacityMonitor capacity, ManifestStore manifest)
    {
        if (!BearerMatches(ctx.Request, options.Value.MetricsBearerToken))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        var current = capacity.Current;
        metrics.StorageAvailable.Set(current.Available);
        metrics.StorageUsed.Set(current.Used);
        metrics.BlobCount.Set(manifest.CountBlobs());
        metrics.UpdateRates();
        ctx.Response.ContentType = PrometheusConstants.TextContentTypeWithVersionAndEncoding;
        await metrics.Registry.CollectAndExportAsTextAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    /// <summary>Constant-time bearer comparison; an unset token refuses every scrape.</summary>
    internal static bool BearerMatches(HttpRequest request, string token)
    {
        const string scheme = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        return token.Length > 0 && header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) &&
            CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(header[scheme.Length..].Trim())),
                SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}

/// <summary>Set when the node starts shutting down, so <c>/ready</c> reports draining (#1 FR-28).</summary>
public sealed class NodeShutdownState
{
    private volatile bool _draining;

    public bool IsDraining => _draining;

    public void BeginDrain() => _draining = true;
}

/// <summary>Probes the storage path and manifest in the background so <c>/ready</c> never does I/O (#1 FR-27).</summary>
public sealed class StorageWritableMonitor(LocalBlobStore blobs, ManifestStore manifest) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    private volatile bool _writable;

    public bool IsWritable => _writable;

    public void Check() => _writable = blobs.IsRootAccessible() && manifest.IsAccessible();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            Check();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
