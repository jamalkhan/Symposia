using Microsoft.AspNetCore.Server.Kestrel.Core;
using Symposia.BlobStorage.StorageNode.Grpc;
using Symposia.BlobStorage.StorageNode.Storage;
using Symposia.Network.Node;
using Symposia.Network.Node.Telemetry;

namespace Symposia.BlobStorage.StorageNode;

/// <summary>
/// Composition root for the storage node, shared by the standalone executable
/// and the single Node Host (issue #133), which hosts it in-process with
/// configuration supplied by <paramref name="configure"/>.
/// </summary>
public static class StorageNodeApp
{
    public static WebApplication Create(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(options);
        configure?.Invoke(builder);

        // With a dedicated gRPC port, plain HTTP (handshake, health, services) and gRPC get separate
        // listeners, because cleartext HTTP/2 isn't negotiated on a mixed HTTP/1.1+2 listener (issue #135).
        // gRPC is served over TLS (#142) and, during the overlap with the previous release, also in
        // cleartext (#145); each is advertised as a protocol feature.
        // Listener settings are needed before the container exists, so bind the options once here (#142 review).
        var nodeOptions = builder.Configuration.GetSection("StorageNode").Get<StorageNodeOptions>() ?? new StorageNodeOptions();
        var listenPort = nodeOptions.ListenPort;
        var grpcPort = nodeOptions.GrpcPort;
        // TLS (issue #142): a self-signed certificate bound to the node key by a proof in
        // /v1/node/services, which gateways pin. It needs the node identity, so Kestrel reads it lazily.
        var grpcTlsPort = nodeOptions.GrpcTlsPort;
        var grpcTls = grpcTlsPort > 0;
        var dedicatedGrpc = grpcPort > 0 || grpcTls;
        if (grpcTls && grpcTlsPort == grpcPort)
        {
            throw new InvalidOperationException("StorageNode:GrpcTlsPort must differ from StorageNode:GrpcPort.");
        }

        StorageTlsCertificateProvider? tls = null;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (!dedicatedGrpc)
            {
                kestrel.ListenAnyIP(listenPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
                return;
            }

            kestrel.ListenAnyIP(listenPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
            if (grpcPort > 0)
            {
                kestrel.ListenAnyIP(grpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
            }

            if (grpcTls)
            {
                kestrel.ListenAnyIP(grpcTlsPort, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                    listenOptions.UseHttps(https => https.ServerCertificateSelector = (_, _) => tls!.Current.Certificate);
                });
            }
        });

        builder.Services.Configure<StorageNodeOptions>(builder.Configuration.GetSection("StorageNode"));
        builder.Services.AddSingleton<LocalBlobStore>();
        if (grpcTls)
        {
            builder.Services.AddSingleton<StorageTlsCertificateProvider>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<StorageTlsCertificateProvider>());
        }
        builder.Services.AddSingleton<ManifestStore>();
        builder.Services.AddHostedService<IntegritySelfCheckWorker>();

        // Node identity, on-chain registration, peer discovery, and handshake (issue #129),
        // configured from this node's own "StorageNode" section.
        builder.Services.AddNodeNetwork(builder.Configuration.GetSection("StorageNode"));
        // Signed 5-minute heartbeats with this node's capacity readings (issue #75).
        builder.Services.AddSingleton<IHeartbeatMetricsSource, StorageHeartbeatMetricsSource>();
        builder.Services.AddTelemetryHeartbeats(builder.Configuration.GetSection("StorageNode"));
        // Storage RPCs only from registered members of this node's network (issue #140).
        if (nodeOptions.RequireCallerAuth)
        {
            builder.Services.AddSingleton(sp =>
            {
                var callers = new StorageRpcCallerRegistry(sp.GetRequiredService<INodeRegistryClient>());
                var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Symposia.Network.Node.StorageRpcAuth");
                callers.AcceptedDuringOutage += caller =>
                    log.LogWarning("Registry unreachable: accepting {Caller} on its last confirmed membership.", caller);
                return callers;
            });
            builder.Services.AddSingleton<StorageRpcAuthInterceptor>();
            builder.Services.AddGrpc(grpc => grpc.Interceptors.Add<StorageRpcAuthInterceptor>());
        }
        else
        {
            builder.Services.AddGrpc();
        }

        var app = builder.Build();

        app.Services.GetRequiredService<LocalBlobStore>().EnsureRootExists();
        app.Services.GetRequiredService<ManifestStore>().Initialize();
        app.UseNodeNetwork();

        // Both gRPC listeners share the service and its caller-auth interceptor (#140).
        var grpc = app.MapGrpcService<StorageNodeGrpcService>();
        if (dedicatedGrpc)
        {
            grpc.RequireHost(new[] { grpcPort, grpcTlsPort }.Where(p => p > 0).Select(p => $"*:{p}").ToArray());
        }

        // Where this node's services live, so a gateway that found it in the registry can reach
        // its gRPC API (issue #135). A hint only: callers check identity via Probe's NodeId.
        if (grpcTls)
        {
            tls = app.Services.GetRequiredService<StorageTlsCertificateProvider>();
            tls.EnsureLoaded();
        }

        var features = new List<string>();
        if (grpcPort > 0 || !dedicatedGrpc)
        {
            features.Add(ProtocolFeatures.StorageGrpcPlaintext);
        }

        if (grpcTls)
        {
            features.Add(ProtocolFeatures.StorageGrpcTls);
        }

        if (nodeOptions.RequireCallerAuth)
        {
            features.Add(ProtocolFeatures.StorageRpcAuthV1);
        }

        app.MapGet(NodeServices.Path, (NodeIdentity identity, Microsoft.Extensions.Options.IOptions<NodeNetworkOptions> network) =>
        {
            var advertised = network.Value.AdvertisedEndpoint;
            var current = tls?.Current;
            var plaintext = grpcPort > 0 || !dedicatedGrpc ? NodeServices.GrpcUrl(advertised, listenPort, grpcPort) : null;
            var secure = grpcTls ? NodeServices.GrpcUrl(advertised, listenPort, grpcTlsPort, tls: true) : null;
            return Results.Ok(new NodeServices(identity.NodeId,
                new NodeServices.StorageService(plaintext, current?.Fingerprint, current?.Proof, current?.NotAfterUnixSeconds, secure),
                ProtocolFeatures.ProtocolVersion, features));
        });

        app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }));

        app.MapGet("/healthz/ready", (LocalBlobStore blobStore, ManifestStore manifestStore) =>
        {
            var ready = blobStore.IsRootAccessible() && manifestStore.IsAccessible();
            return ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });

        return app;
    }

    public static WebApplication Create(string[] args) => Create(new WebApplicationOptions { Args = args });
}
