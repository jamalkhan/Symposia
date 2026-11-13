using Microsoft.Extensions.DependencyInjection.Extensions;
using Symposia.BlobStorage.Gateway.History;
using Symposia.BlobStorage.Gateway.Api;
using Symposia.BlobStorage.Gateway;
using Symposia.BlobStorage.Gateway.Admin;
using Symposia.BlobStorage.Gateway.Auth;
using Symposia.BlobStorage.Gateway.GC;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Quorum;
using Symposia.BlobStorage.Gateway.Repair;
using Symposia.BlobStorage.Gateway.S3;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway;

/// <summary>
/// Composition root for the S3 gateway, shared by the standalone executable and the
/// Node Host's blob-gateway role (issue #135), which supplies configuration through
/// <paramref name="configure"/>.
/// </summary>
public static class BlobGatewayApp
{
    public static WebApplication Create(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(options);
        configure?.Invoke(builder);

        // ── Configuration ────────────────────────────────────────────────────────────

        builder.Services.Configure<GatewayOptions>(
            builder.Configuration.GetSection("Gateway"));

        // ── Services ─────────────────────────────────────────────────────────────────

        builder.Services.AddSingleton<CredentialStore>();
        builder.Services.AddSingleton<SigV4Verifier>();
        builder.Services.AddSingleton<GatewayMetadataStore>();

        // Operation history (#204): its own DB file beside the metadata DB, written off the request path.
        var gatewayConfig = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
        var trustedProxies = gatewayConfig.TrustedProxies.Select(p => Symposia.Network.Node.ClientAddress.TryParseNetwork(p, out var n)
            ? n : throw new InvalidOperationException($"Gateway:TrustedProxies has an invalid entry: '{p}'.")).ToList();
        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;
            return new History.HistoryStore(o.History.DbPath.Length > 0 ? o.History.DbPath
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.MetadataDbPath)) ?? ".", "gateway-history.db"));
        });
        builder.Services.AddSingleton<History.HistoryRecorder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<History.HistoryRecorder>());

        // NodeRegistry: INodeRegistry + IHostedService (background health probing).
        // The IHostedService delegate resolves NodeRegistry, so NodeRegistry must stay registered.
        // INodeRegistry is separately registered so QuorumWriter, endpoints, and background services
        // can inject it without knowing the concrete type (and tests can replace it).
        builder.Services.AddSingleton<NodeRegistry>();
        builder.Services.AddSingleton<INodeRegistry>(sp => sp.GetRequiredService<NodeRegistry>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeRegistry>());

        builder.Services.AddScoped<QuorumWriter>();

        // Repair / resilience services.
        builder.Services.AddSingleton<RepairQueue>();
        builder.Services.AddScoped<Objects.ObjectService>(); // scoped: it uses the scoped QuorumWriter
        builder.Services.AddSingleton<NodeReconciler>();
        builder.Services.AddHostedService<GcWorker>();
        builder.Services.AddHostedService<MultipartReaper>();
        builder.Services.AddHostedService<RepairWorker>();
        builder.Services.AddSingleton<ReplicationMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ReplicationMonitor>());
        builder.Services.AddHostedService<ReconcilePollerService>();

        // Which storage nodes may be used, from the network's on-chain protocol activations (issue #145).
        // With a static node source there is no chain to read, so nothing is required.
        builder.Services.AddSingleton<NetworkPolicy>();
        builder.Services.AddSingleton<INetworkPolicy>(sp => sp.GetRequiredService<NetworkPolicy>());

        // Where storage nodes come from (issues #135, #137): the on-chain registry, or static config.
        if (builder.Configuration.GetValue("Gateway:NodeSource", NodeSourceKind.Static) == NodeSourceKind.Registry)
        {
            var chainGateway = builder.Configuration["Gateway:BlockchainGatewayUrl"];
            if (string.IsNullOrWhiteSpace(chainGateway))
            {
                throw new InvalidOperationException("Gateway:BlockchainGatewayUrl is required when Gateway:NodeSource is Registry.");
            }

            builder.Services.AddHttpClient<INodeRegistryClient, NodeRegistryClient>(http =>
            {
                http.BaseAddress = new Uri(chainGateway);
                http.Timeout = TimeSpan.FromSeconds(10);
            });
            builder.Services.AddHttpClient(RegistryNodeSource.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(3));
            builder.Services.AddSingleton<IStorageNodeSource, RegistryNodeSource>();
            builder.Services.AddHttpClient(ProtocolScheduleSyncService.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(5));
            // Before NodeSourceSyncService, so the first node sync already applies the schedule.
            builder.Services.AddHostedService<ProtocolScheduleSyncService>();

            // Sign storage RPCs with the host's node key (issue #140). The host's key already exists;
            // never generate one here, or the gateway would sign as an unregistered stranger.
            var keyPath = builder.Configuration["Gateway:NodeIdentityKeyPath"];
            if (!string.IsNullOrWhiteSpace(keyPath))
            {
                builder.Services.AddSingleton<IStorageRpcSigner>(sp =>
                {
                    var identity = new NodeIdentity(Microsoft.Extensions.Options.Options.Create(new NodeNetworkOptions
                    {
                        NodeIdentityKeyPath = keyPath,
                        ChainId = builder.Configuration.GetValue<ulong>("Gateway:ChainId"),
                        NodeRegistryAddress = builder.Configuration["Gateway:NodeRegistryAddress"] ?? "",
                    }));
                    identity.LoadExisting();
                    return new NodeIdentityRpcSigner(identity);
                });
            }
        }
        else
        {
            builder.Services.AddSingleton<IStorageNodeSource, StaticNodeSource>();
        }

        builder.Services.AddHostedService<NodeSourceSyncService>();

        // On a gateway-only NodeHost the gateway is the node's primary endpoint (#228): peer handshakes, signed with the node key.
        var servesPrimary = builder.Configuration.GetValue("Gateway:ServesPrimary", false);
        if (servesPrimary)
        {
            builder.Services.AddNodeNetwork(builder.Configuration.GetSection("GatewayNode"));
        }

        AddServiceMetrics(builder, gatewayConfig);

        // ── Kestrel: HTTP/1.1 for S3 clients ─────────────────────────────────────────
        // Production terminates TLS externally (load-balancer or sidecar). Cleartext HTTP/2 isn't
        // negotiated on a mixed listener anyway, and S3 SDKs speak HTTP/1.1 (issue #135).
        var listenPort = builder.Configuration.GetValue("Gateway:ListenPort", 5181);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // S3 PUTs are streamed chunk by chunk to the nodes, so whole-object bodies (100 MB+, issue #135)
            // never sit in memory; Kestrel's 30 MB default would reject them.
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.ListenAnyIP(listenPort, lo =>
                lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
        });

        var app = builder.Build();

        // ── Init ──────────────────────────────────────────────────────────────────────

        app.Services.GetRequiredService<GatewayMetadataStore>().Initialize();

        // A node coming online (new, or back from an outage) gets missing copies straight away (issue #135).
        var replicationMonitor = app.Services.GetRequiredService<ReplicationMonitor>();
        app.Services.GetRequiredService<NodeRegistry>().NodeBecameHealthy += _ => replicationMonitor.RequestSweep();
        // Nodes refused by network policy (#145): re-replicate their objects now, not at the next scan.
        app.Services.GetRequiredService<NodeRegistry>().NodesDropped += _ => replicationMonitor.RequestSweep();

        // ── SigV4 middleware ──────────────────────────────────────────────────────────

        app.UseRequestIds();
        if (servesPrimary)
        {
            app.UseNodeNetwork();
        }

        // Runs before routing. Exempt health and admin endpoints, and the /v1 API's OpenAPI document.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/healthz/", StringComparison.Ordinal) ||
                path.StartsWith("/admin/", StringComparison.Ordinal) ||
                path == BlobApiEndpoints.OpenApiPath ||
                (servesPrimary && Symposia.Network.Node.PrimaryEndpointPaths.Contains(path)))
            {
                await next();
                return;
            }

            var verifier = ctx.RequestServices.GetRequiredService<SigV4Verifier>();
            var sigv4 = verifier.Authenticate(ctx.Request);
            var tenantId = sigv4?.TenantId;
            if (tenantId is null && ctx.Request.Path.StartsWithSegments(BlobApiEndpoints.Prefix))
            {
                await BlobApiEndpoints.Error(401, "AccessDenied", "Missing or invalid AWS Signature V4.").ExecuteAsync(ctx);
                return;
            }

            if (tenantId is null)
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.ContentType = "application/xml";
                await ctx.Response.WriteAsync(
                    "<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>");
                return;
            }

            ctx.Items["TenantId"] = tenantId;
            ctx.Items[nameof(SigV4Context)] = sigv4;
            await next();
        });

        // After auth, so each recorded operation carries its caller (#204).
        app.UseHistoryCapture(trustedProxies);

        // ── Health endpoints ──────────────────────────────────────────────────────────

        app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }));

        app.MapGet("/healthz/ready", (INodeRegistry nodes) =>
        {
            var healthy = nodes.Healthy;
            return healthy.Count > 0
                ? Results.Ok(new { status = "ready", nodes = healthy.Count })
                : Results.StatusCode(503);
        });

        // ── Admin endpoints ───────────────────────────────────────────────────────────

        AdminEndpoints.Map(app);

        // Local diagnostics for the last epoch this gateway measured (#221 FR4); admin paths skip SigV4.
        app.MapGet("/admin/service-metrics", (HttpContext ctx, History.HistoryStore store) =>
            ctx.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip) ? Results.NotFound()
            : store.LatestMeasurement() is { } m
                ? Results.Ok(new { m.Epoch, m.Factors.UptimeBps, m.Factors.SuccessBps, m.Factors.P95Ms, m.Factors.BytesServed, m.Factors.Requests })
                : Results.NoContent());

        // What the Verifier measures this gateway through (#225); /healthz paths skip SigV4 and history.
        Rewards.ProbeEndpoints.Map(app, trustedProxies);

        // ── S3 routes ─────────────────────────────────────────────────────────────────

        // ── Symposia-native API (#203) ────────────────────────────────────────────────

        // The literal /v1 segment outranks S3's {bucket} parameter; registered first for clarity.
        BlobApiEndpoints.Map(app);

        // Object routes must be registered before bucket routes so /{bucket}/{**key}
        // takes priority over /{bucket} when a key is present.
        MultipartEndpoints.Map(app);
        ObjectEndpoints.Map(app);
        BucketEndpoints.Map(app);
        return app;
    }

    /// <summary>
    /// What the Verifier measures this gateway through (#225): the probe canary and endpoints need the node key; the
    /// epoch recorder, the signed epoch figure and the probe's reserved lane need the chain. Without them each part
    /// stays off with one warning. The gateway never signs a metric report.
    /// </summary>
    private static void AddServiceMetrics(WebApplicationBuilder builder, GatewayOptions gateway)
    {
        var o = gateway.ServiceMetrics;
        if (!string.IsNullOrWhiteSpace(gateway.NodeIdentityKeyPath))
        {
            builder.Services.TryAddSingleton(_ =>
            {
                var identity = new NodeIdentity(Microsoft.Extensions.Options.Options.Create(new NodeNetworkOptions { NodeIdentityKeyPath = gateway.NodeIdentityKeyPath }));
                identity.LoadExisting();
                return identity;
            });
            builder.Services.AddHostedService<Rewards.CanaryService>();
        }

        builder.Services.AddSingleton(sp => new Rewards.ProbeLimiter(sp.GetService<Rewards.IServiceMetricsChain>(), sp.GetRequiredService<ILogger<Rewards.ProbeLimiter>>()));
        builder.Services.Configure<Rewards.ServiceMetricsOptions>(builder.Configuration.GetSection("Gateway:ServiceMetrics"));
        if (!o.Enabled)
        {
            return;
        }

        string[] missing = [.. new[] { ("RpcUrl", o.RpcUrl), ("RewardDistributorAddress", o.RewardDistributorAddress), ("EpochControllerAddress", o.EpochControllerAddress) }
            .Where(p => string.IsNullOrWhiteSpace(p.Item2)).Select(p => p.Item1)];
        if (missing.Length > 0)
        {
            builder.Services.AddHostedService(sp => new LogOnce(sp.GetRequiredService<ILogger<Rewards.EpochFactorRecorder>>(),
                $"Epoch measurement is off: missing Gateway:ServiceMetrics {string.Join(", ", missing)}."));
            return;
        }

        builder.Services.AddSingleton<Rewards.IServiceMetricsChain>(new Rewards.NethereumServiceMetricsChain(o));
        builder.Services.AddHostedService<Rewards.EpochFactorRecorder>();
    }

    private sealed class LogOnce(ILogger logger, string message) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            logger.LogWarning("{Message}", message);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    public static WebApplication Create(string[] args) => Create(new WebApplicationOptions { Args = args });
}
