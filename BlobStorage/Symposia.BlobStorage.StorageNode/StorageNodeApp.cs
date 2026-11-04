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
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (grpcPort > 0)
            {
                kestrel.ListenAnyIP(listenPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
                kestrel.ListenAnyIP(grpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
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
            builder.Services.AddSingleton<StorageRpcCallerRegistry>();
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
        app.MapGet(NodeServices.Path, (NodeIdentity identity, Microsoft.Extensions.Options.IOptions<NodeNetworkOptions> network) =>
            Results.Ok(new NodeServices(identity.NodeId, new NodeServices.StorageService(
                NodeServices.GrpcUrl(network.Value.AdvertisedEndpoint, listenPort, grpcPort)))));

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
