using Symposia.Blockchain.Gateway.Chain;
using Symposia.Blockchain.Gateway.Nodes;
using Symposia.Blockchain.Gateway.Rpc;
using Symposia.Blockchain.Gateway.Sealing;
using Symposia.Blockchain.Gateway.Telemetry;
using Symposia.Network.Node.Telemetry.Committee;

namespace Symposia.Blockchain.Gateway;

/// <summary>
/// Composition root for the Bootstrap Chain Gateway, shared by the standalone
/// executable and the single Node Host (issue #133), which hosts it in-process
/// with configuration supplied by <paramref name="configure"/>.
/// </summary>
public static class GatewayApp
{
    public static WebApplication Create(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(options);
        configure?.Invoke(builder);

        builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
        builder.Services.AddSingleton<RelayerAccount>();
        builder.Services.AddSingleton<BootstrapChainClient>();

        var gateway = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
        if (gateway.EpochSealer.Enabled && gateway.RewardDistributorAddress.Length > 0 && gateway.EpochControllerAddress.Length > 0)
        {
            builder.Services.AddSingleton<IEpochSealChain, EpochSealChain>();
            builder.Services.AddSingleton<EpochSealer>();
            builder.Services.AddHostedService<EpochSealerService>();
        }

        if (gateway.RpcProxy.Enabled)
        {
            if (gateway.RpcProxy.Validate() is { } invalid)
            {
                throw new InvalidOperationException(invalid);
            }

            builder.Services.AddRpcProxy(gateway.RpcProxy);
        }

        builder.Services.AddSingleton<ITelemetryBlobStore>(sp =>
        {
            var blobs = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.TelemetryBlobs;
            ITelemetryBlobStore? remote = string.IsNullOrEmpty(blobs.Endpoint) ? null : new S3TelemetryBlobStore(new HttpClient(), blobs);
            return new CachingTelemetryBlobStore(string.IsNullOrEmpty(blobs.CacheDirectory) ? null : blobs.CacheDirectory, remote);
        });

        var app = builder.Build();

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapNodeEndpoints();
        app.MapTelemetryRelay();
        if (gateway.RpcProxy.Enabled)
        {
            app.MapRpcProxy();
        }

        return app;
    }

    public static WebApplication Create(string[] args) => Create(new WebApplicationOptions { Args = args });
}
