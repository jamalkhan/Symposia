using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Symposia.Network.Node;
using Symposia.NodeHost.Diagnostics;

namespace Symposia.NodeHost.Components;

/// <summary>
/// Host-level node networking (issue #133 FR-D3): the single writer of this
/// node's on-chain registration (one primary endpoint) and the single peer
/// discovery loop, whose peer table the diagnostics page shows.
/// </summary>
public sealed class NetworkComponent : HostComponent
{
    private IHost? _host;

    public override string Name => "network";

    public IServiceProvider? Services => _host?.Services;

    protected override string? SkipReason(HostContext context) =>
        context.Network is null ? "Waiting for the network (see chain component)" : null;

    protected override async Task OnStartAsync(HostContext context, CancellationToken ct)
    {
        var network = context.Network!;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:NodeIdentityKeyPath"] = context.Paths.NodeKey,
            ["Node:BlockchainGatewayUrl"] = network.GatewayUrl,
            ["Node:NodeRegistryAddress"] = network.NodeRegistryAddress,
            ["Node:ChainId"] = network.ChainId.ToString(),
            ["Node:AdvertisedEndpoint"] = context.PrimaryEndpoint ?? "",
            ["Node:DiscoveryIntervalSeconds"] = context.Settings.Discovery.IntervalSeconds.ToString(),
        });
        for (var i = 0; i < context.Settings.Discovery.SeedPeers.Length; i++)
        {
            builder.Configuration[$"Node:SeedPeers:{i}"] = context.Settings.Discovery.SeedPeers[i];
        }

        builder.Logging.AddProvider(new ComponentLoggerProvider(Name, context.Logs));
        builder.Services.AddNodeNetwork(builder.Configuration.GetSection("Node"));

        _host = builder.Build();
        _host.Services.GetRequiredService<NodeIdentity>().EnsureLoadedOrGenerated();
        await _host.StartAsync(ct);
    }

    protected override async Task OnStopAsync()
    {
        var host = _host;
        _host = null;
        if (host is not null)
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
