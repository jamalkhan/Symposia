using Symposia.Blockchain.Gateway;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Components;

/// <summary>The Bootstrap Chain Gateway, hosted in-process in LocalDev and RemoteRpc modes (FR-C1).</summary>
public sealed class GatewayComponent : WebAppComponent
{
    public override string Name => "gateway";

    protected override string? SkipReason(HostContext context) =>
        context.Settings.Network.Mode switch
        {
            NetworkMode.Unconfigured => "Network not configured yet",
            NetworkMode.Join => "Using the remote gateway (Join mode)",
            _ => base.SkipReason(context),
        };

    protected override WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder> configure, HostContext context) =>
        GatewayApp.Create(options, configure);

    protected override int Port(HostContext context) => context.Settings.Network.GatewayPort;

    protected override IDictionary<string, string?> Configuration(HostContext context)
    {
        var network = context.Network!;
        return new Dictionary<string, string?>
        {
            ["Gateway:RpcUrl"] = network.RpcUrl,
            ["Gateway:NodeRegistryAddress"] = network.NodeRegistryAddress,
            ["Gateway:EpochRootRegistryAddress"] = network.EpochRootRegistryAddress,
            ["Gateway:ProtocolActivationsAddress"] = network.ProtocolActivationsAddress,
            // Both set = the gateway runs the epoch sealer (issue #72) with the relayer key.
            ["Gateway:RewardDistributorAddress"] = network.RewardDistributorAddress,
            ["Gateway:EpochControllerAddress"] = network.EpochControllerAddress,
            // Telemetry relays (#158); relayed self-checkpoint batches are kept under the gateway's data dir.
            ["Gateway:TelemetryRegistryAddress"] = network.TelemetryRegistryAddress,
            ["Gateway:TelemetryBlobs:CacheDirectory"] = Path.Combine(context.Paths.DataDir, "telemetry-blobs"),
            ["Gateway:RelayerPrivateKey"] = context.Settings.Network.Mode == NetworkMode.LocalDev
                ? ChainComponent.AnvilDevKey
                : context.Secrets.RelayerPrivateKey,
        };
    }
}
