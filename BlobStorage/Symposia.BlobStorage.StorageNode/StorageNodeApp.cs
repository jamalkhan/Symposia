using Microsoft.AspNetCore.Server.Kestrel.Core;
using Symposia.BlobStorage.StorageNode.Grpc;
using Symposia.BlobStorage.StorageNode.Storage;
using Symposia.Network.Node;

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

        // Production deployments terminate TLS 1.3 in front of this (Requirements/Platform/security.md#encryption-in-transit).
        // With GrpcPort set, plain HTTP (handshake, health, services) and gRPC get separate listeners,
        // because cleartext HTTP/2 isn't negotiated on a mixed HTTP/1.1+2 listener (issue #135).
        var listenPort = builder.Configuration.GetValue("StorageNode:ListenPort", 5180);
        var grpcPort = builder.Configuration.GetValue("StorageNode:GrpcPort", 0);
        // TLS on the gRPC port (issue #142): a self-signed certificate bound to the node key by a proof
        // in /v1/node/services, which gateways pin. Loaded after the identity, so Kestrel reads it lazily.
        var grpcTls = grpcPort > 0 && builder.Configuration.GetValue("StorageNode:GrpcTls", false);
        var tls = new StorageTlsState();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (grpcPort > 0)
            {
                kestrel.ListenAnyIP(listenPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
                kestrel.ListenAnyIP(grpcPort, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                    if (grpcTls)
                    {
                        listenOptions.UseHttps(https => https.ServerCertificateSelector = (_, _) => tls.Certificate);
                    }
                });
            }
            else
            {
                kestrel.ListenAnyIP(listenPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
            }
        });

        builder.Services.Configure<StorageNodeOptions>(builder.Configuration.GetSection("StorageNode"));
        builder.Services.AddSingleton<LocalBlobStore>();
        builder.Services.AddSingleton<ManifestStore>();
        builder.Services.AddHostedService<IntegritySelfCheckWorker>();

        // Node identity, on-chain registration, peer discovery, and handshake (issue #129),
        // configured from this node's own "StorageNode" section.
        builder.Services.AddNodeNetwork(builder.Configuration.GetSection("StorageNode"));
        // Storage RPCs only from registered members of this node's network (issue #140).
        if (builder.Configuration.GetValue("StorageNode:RequireCallerAuth", false))
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

        var grpc = app.MapGrpcService<StorageNodeGrpcService>();
        if (grpcPort > 0)
        {
            grpc.RequireHost($"*:{grpcPort}");
        }

        // Where this node's services live, so a gateway that found it in the registry can reach
        // its gRPC API (issue #135). A hint only: callers check identity via Probe's NodeId.
        if (grpcTls)
        {
            var identity = app.Services.GetRequiredService<NodeIdentity>();
            identity.EnsureLoadedOrGenerated();
            var storageOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageNodeOptions>>().Value;
            var path = string.IsNullOrWhiteSpace(storageOptions.TlsCertificatePath)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storageOptions.ManifestDbPath))!, "grpc-tls.pfx")
                : storageOptions.TlsCertificatePath;
            tls.Certificate = StorageTlsCertificate.LoadOrCreate(path, identity.NodeId,
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Symposia.BlobStorage.StorageNode.Tls"));
            tls.Fingerprint = StorageTlsCertificate.Fingerprint(tls.Certificate);
            tls.Proof = identity.SignStorageTls(Convert.FromHexString(tls.Fingerprint));
        }

        app.MapGet(NodeServices.Path, (NodeIdentity identity, Microsoft.Extensions.Options.IOptions<NodeNetworkOptions> network) =>
            Results.Ok(new NodeServices(identity.NodeId, new NodeServices.StorageService(
                NodeServices.GrpcUrl(network.Value.AdvertisedEndpoint, listenPort, grpcPort, grpcTls), tls.Fingerprint, tls.Proof))));

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

/// <summary>The gRPC port's certificate and its published identity binding (#142); empty when TLS is off.</summary>
internal sealed class StorageTlsState
{
    public System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate { get; set; }

    public string? Fingerprint { get; set; }

    public string? Proof { get; set; }
}
