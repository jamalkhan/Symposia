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
        // The Credential Authority (#3): its state is cached here and enforced per request; uses are audited there.
        builder.Services.AddHttpClient(CredentialSync.HttpClientName, (sp, http) =>
        {
            var authority = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.CredentialAuthority;
            if (authority.Url.Length > 0) http.BaseAddress = new Uri(authority.Url.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add("X-Service-Token", authority.ServiceToken);
            http.Timeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.AddHttpClient(CredentialSync.HttpClientName + "-grpc", http => http.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddSingleton<CredentialSync>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CredentialSync>());
        builder.Services.AddSingleton<AuditForwarder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditForwarder>());
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

        builder.Services.AddSingleton<Placement.PlacementEngine>();
        builder.Services.AddSingleton<Placement.RegionAssignmentResolver>();
        builder.Services.AddScoped<QuorumWriter>();
        builder.Services.AddSingleton<TenantQuota>();
        builder.Services.AddSingleton<IWriteQuorumRequirement, ConfiguredWriteQuorum>();

        // Health, readiness, metrics and drain (#1).
        builder.Services.AddSingleton<Health.GatewayMetrics>();
        builder.Services.AddSingleton<Health.ShutdownState>();
        builder.Services.AddSingleton<Health.MetadataIndexMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Health.MetadataIndexMonitor>());
        builder.Services.Configure<HostOptions>(host =>
            host.ShutdownTimeout = TimeSpan.FromSeconds(Math.Max(1, gatewayConfig.ShutdownTimeoutSeconds)));

        // Repair / resilience services.
        builder.Services.AddSingleton<RepairQueue>(sp => new RepairQueue(sp.GetRequiredService<GatewayMetadataStore>()));
        builder.Services.AddSingleton<BandwidthBudget>(sp => new BandwidthBudget(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()));
        builder.Services.AddSingleton<Erasure.ErasureObjects>();

        // Penalty stages (#8 FR3): operator-set until the on-chain penalty system lands; its reactions run here.
        builder.Services.AddSingleton<Penalties.ManualPenaltySource>();
        builder.Services.AddSingleton<Penalties.INodePenaltyEventSource>(sp => sp.GetRequiredService<Penalties.ManualPenaltySource>());
        builder.Services.AddSingleton<Penalties.IIntegrityFailureSink, Penalties.LoggingIntegrityFailureSink>();
        builder.Services.AddSingleton<Penalties.PenaltyResponder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Penalties.PenaltyResponder>());
        builder.Services.AddSingleton<Integrity.ChallengeScheduler>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Integrity.ChallengeScheduler>());
        builder.Services.AddScoped<Objects.ObjectService>(); // scoped: it uses the scoped QuorumWriter
        builder.Services.AddSingleton<NodeReconciler>();
        builder.Services.AddSingleton<SafetyInvariantGate>();
        builder.Services.AddSingleton<IOnChainMetadataOracle, FailClosedOracle>(); // fail-closed until the chain layer answers (#16 FR5.3)
        builder.Services.AddHostedService<GcWorker>();
        builder.Services.AddHostedService<GcEvaluator>();
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
        app.Services.GetRequiredService<Health.MetadataIndexMonitor>().Check();
        ClaimUnownedBuckets(app);

        // Drain (#1 FR-28): /ready goes 503 first, so the load balancer stops sending new connections
        // during the notice period; Kestrel then stops listening and in-flight requests finish within
        // HostOptions.ShutdownTimeout.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            app.Services.GetRequiredService<Health.ShutdownState>().BeginDrain();
            var notice = gatewayConfig.DrainNoticeSeconds;
            if (notice > 0)
            {
                app.Logger.LogInformation("Draining: reporting not ready for {Notice}s before closing listeners.", notice);
                Thread.Sleep(TimeSpan.FromSeconds(notice));
            }
        });

        // A node coming online (new, or back from an outage) gets missing copies straight away (issue #135).
        var replicationMonitor = app.Services.GetRequiredService<ReplicationMonitor>();
        app.Services.GetRequiredService<NodeRegistry>().NodeBecameHealthy += node =>
        {
            app.Services.GetRequiredService<GatewayMetadataStore>().ClearOffline(node.Id); // back: no longer Offline (#16 FR4)
            replicationMonitor.RequestSweep();
        };
        // Nodes refused by network policy (#145): re-replicate their objects now, not at the next scan.
        app.Services.GetRequiredService<NodeRegistry>().NodesDropped += _ => replicationMonitor.RequestSweep();

        // ── SigV4 middleware ──────────────────────────────────────────────────────────

        app.UseRequestIds();
        Health.RequestLog.UseRequestLog(app);
        if (servesPrimary)
        {
            app.UseNodeNetwork();
        }

        // Runs before routing. Exempt health and admin endpoints, and the /v1 API's OpenAPI document.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (Health.HealthEndpoints.IsHealthPath(path) ||
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

            // Authorization (#3 FR4), from cached Authority state: revoked, suspended, expired, region-locked,
            // cross-tenant, out-of-scope and policy-denied requests stop here, each with its own code.
            var cache = ctx.RequestServices.GetRequiredService<CredentialStore>();
            // Gone between authentication and here (a sync just removed it): refuse, never crash (review: Carl #5).
            if (cache.Grant(sigv4!.AccessKeyId) is not { } grant)
            {
                await Authorization.Refusal(ctx, Symposia.BlobStorage.Domain.Credentials.AccessDecision.CredentialRevoked).ExecuteAsync(ctx);
                return;
            }

            var gatewayOptions = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;
            var decision = Authorization.Decide(ctx, grant, cache, ctx.RequestServices.GetRequiredService<GatewayMetadataStore>(),
                gatewayOptions.Region, sigv4.Scope.Split('/') is { Length: > 1 } scope ? scope[1] : null, DateTimeOffset.UtcNow);
            ctx.Items[Health.RequestLog.CredentialItem] = grant.CredentialId;
            var audit = ctx.RequestServices.GetRequiredService<AuditForwarder>();
            Symposia.BlobStorage.Domain.Credentials.UseReport Use(string outcome, string? reason) => new(grant.CredentialId, $"{ctx.Request.Method} {Authorization.Classify(ctx.Request) switch
            {
                (null, _, _, _) => "service",
                (_, null, _, _) => "bucket",
                _ => "object",
            }}", ctx.Request.Path.Value, ctx.Connection.RemoteIpAddress?.ToString(), gatewayOptions.Region,
                gatewayOptions.CredentialAuthority.GatewayId is { Length: > 0 } id ? id : Environment.MachineName, outcome, reason, DateTimeOffset.UtcNow);
            if (decision != Symposia.BlobStorage.Domain.Credentials.AccessDecision.Allowed)
            {
                audit.Report(Use("Rejected", decision.ToString()));
                Health.RequestLog.Error(ctx, decision.ToString());
                await Authorization.Refusal(ctx, decision).ExecuteAsync(ctx);
                return;
            }

            ctx.Items["TenantId"] = tenantId;
            ctx.Items[nameof(SigV4Context)] = sigv4;
            await next();
            audit.Report(Use(ctx.Response.StatusCode < 400 ? "Success" : "Failed", ctx.Response.StatusCode < 400 ? null : ctx.Response.StatusCode.ToString()));
        });

        // After auth, so each recorded operation carries its caller (#204).
        app.UseHistoryCapture(trustedProxies);

        // ── Health endpoints ──────────────────────────────────────────────────────────

        Health.HealthEndpoints.Map(app);

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
        Authorization.MapManagement(app);

        // Object routes must be registered before bucket routes so /{bucket}/{**key}
        // takes priority over /{bucket} when a key is present.
        MultipartEndpoints.Map(app);
        ObjectEndpoints.Map(app);
        BucketEndpoints.Map(app);
        return app;
    }

    /// <summary>
    /// Buckets created before ownership existed have no tenant. A single-tenant deployment (one tenant in
    /// its configured credentials) adopts them on upgrade, so its existing buckets stay usable under tenant
    /// isolation (#3); with several tenants the operator assigns them, and they stay unusable until then.
    /// </summary>
    private static void ClaimUnownedBuckets(WebApplication app)
    {
        var tenants = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.Credentials
            .Select(c => c.TenantId).Where(t => t.Length > 0).Distinct().ToList();
        if (tenants.Count == 1 && app.Services.GetRequiredService<GatewayMetadataStore>().ClaimUnownedBuckets(tenants[0]) is var claimed and > 0)
        {
            app.Logger.LogInformation("Assigned {Count} bucket(s) created before bucket ownership to tenant {Tenant}.", claimed, tenants[0]);
        }
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
