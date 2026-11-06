using Symposia.Blockchain.Gateway.Chain;
using Symposia.Blockchain.Gateway.Nodes;
using Symposia.Blockchain.Gateway.Sealing;

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
        builder.Services.AddSingleton<BootstrapChainClient>();

        var gateway = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
        if (gateway.EpochSealer.Enabled && gateway.RewardDistributorAddress.Length > 0 && gateway.EpochControllerAddress.Length > 0)
        {
            builder.Services.AddSingleton<IEpochSealChain, EpochSealChain>();
            builder.Services.AddSingleton<EpochSealer>();
            builder.Services.AddHostedService<EpochSealerService>();
        }

        var app = builder.Build();

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapNodeEndpoints();

        return app;
    }

    public static WebApplication Create(string[] args) => Create(new WebApplicationOptions { Args = args });
}
