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

        // h2c (cleartext HTTP/2) so gRPC and the plain HTTP health endpoints share one Kestrel endpoint in dev.
        // Production deployments terminate TLS 1.3 in front of this (Requirements/Platform/security.md#encryption-in-transit).
        var listenPort = builder.Configuration.GetValue("StorageNode:ListenPort", 5180);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(listenPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
        });

        builder.Services.Configure<StorageNodeOptions>(builder.Configuration.GetSection("StorageNode"));
        builder.Services.AddSingleton<LocalBlobStore>();
        builder.Services.AddSingleton<ManifestStore>();
        builder.Services.AddHostedService<IntegritySelfCheckWorker>();

        // Node identity, on-chain registration, peer discovery, and handshake (issue #129),
        // configured from this node's own "StorageNode" section.
        builder.Services.AddNodeNetwork(builder.Configuration.GetSection("StorageNode"));
        builder.Services.AddGrpc();

        var app = builder.Build();

        app.Services.GetRequiredService<LocalBlobStore>().EnsureRootExists();
        app.Services.GetRequiredService<ManifestStore>().Initialize();
        app.UseNodeNetwork();

        app.MapGrpcService<StorageNodeGrpcService>();

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
