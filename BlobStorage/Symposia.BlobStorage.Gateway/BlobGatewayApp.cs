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

        // Runs before routing. Exempt health and admin endpoints.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/healthz/", StringComparison.Ordinal) ||
                path.StartsWith("/admin/", StringComparison.Ordinal))
            {
                await next();
                return;
            }

            var verifier = ctx.RequestServices.GetRequiredService<SigV4Verifier>();
            var sigv4 = verifier.Authenticate(ctx.Request);
            var tenantId = sigv4?.TenantId;
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

        // ── S3 routes ─────────────────────────────────────────────────────────────────

        // Object routes must be registered before bucket routes so /{bucket}/{**key}
        // takes priority over /{bucket} when a key is present.
        MultipartEndpoints.Map(app);
        ObjectEndpoints.Map(app);
        BucketEndpoints.Map(app);
        return app;
    }

    public static WebApplication Create(string[] args) => Create(new WebApplicationOptions { Args = args });
}
