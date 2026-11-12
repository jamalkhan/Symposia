using Microsoft.Extensions.Logging;
using Symposia.Network.Node;
using Symposia.Network.Verifier;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Components;

/// <summary>
/// The Verifier role (#225): the network monitor, hosted in NodeHost's process. It binds no port and doesn't stake.
/// Whether it may sign is decided on-chain (<c>reward.metric.signer</c>), not here. On LocalDev it runs by default
/// with the dev deployer key, which the LocalDev deploy makes the metric signer, so local rewards exercise the
/// same path as production.
/// </summary>
public sealed class VerifierRoleComponent : HostComponent
{
    private VerifierEngine? _engine;
    private HttpClient? _http;
    private HttpClient? _gateway;
    private HttpClient? _targets;

    public override string Name => "verifier";

    /// <summary>The running engine; null while stopped.</summary>
    public VerifierEngine? Engine => _engine;

    protected override string? SkipReason(HostContext context)
    {
        if (context.Network is not { } network)
        {
            return "Waiting for the network (see chain component)";
        }

        if (!context.Settings.Verifier.Enabled && !IsLocalDev(context, network))
        {
            return "Verifier role is turned off";
        }

        if (string.IsNullOrEmpty(network.RpcUrl) || string.IsNullOrEmpty(network.RewardDistributorAddress) || string.IsNullOrEmpty(network.EpochControllerAddress)
            || string.IsNullOrEmpty(network.StakingRegistryAddress))
        {
            return "The network's reward contracts aren't known yet";
        }

        return null;
    }

    protected override Task OnStartAsync(HostContext context, CancellationToken ct)
    {
        var network = context.Network!;
        var keyPath = KeyPath(context);
        if (IsLocalDev(context, network) && !File.Exists(keyPath))
        {
            Symposia.Network.Node.Rewards.LocalKeyMetricAttestor.Write(keyPath, ChainComponent.AnvilDevKey);
        }

        var identity = new NodeIdentity(Microsoft.Extensions.Options.Options.Create(new NodeNetworkOptions { NodeIdentityKeyPath = context.Paths.NodeKey }));
        identity.LoadExisting();
        var options = new VerifierOptions
        {
            ChainId = network.ChainId,
            RpcUrl = network.RpcUrl!,
            BlockchainGatewayUrl = network.GatewayUrl,
            NodeRegistryAddress = network.NodeRegistryAddress,
            StakingRegistryAddress = network.StakingRegistryAddress!,
            RewardDistributorAddress = network.RewardDistributorAddress!,
            EpochControllerAddress = network.EpochControllerAddress!,
            NodeAddress = identity.NodeId,
            SignerKeyPath = keyPath,
            DataDir = context.RoleDataDir(Name),
        };

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _gateway = new HttpClient { BaseAddress = new Uri(network.GatewayUrl), Timeout = TimeSpan.FromMinutes(3) }; // the relay waits for a receipt
        _targets = TargetHttp.Create(options); // probes and directories: no redirects, connect-time address checks off LocalDev
        var logger = context.CreateLogger(Name, "Symposia.Network.Verifier");
        _engine = new VerifierEngine(options, new VerifierStore(Path.Combine(options.DataDir, "verifier.db")),
            new NethereumVerifierChain(options, new NodeRegistryClient(_gateway)), new HttpReportRelay(_gateway), _http, logger, targetHttp: _targets);
        _engine.Start();
        return Task.CompletedTask;
    }

    protected override async Task OnStopAsync()
    {
        if (_engine is { } engine)
        {
            _engine = null;
            await engine.DisposeAsync();
        }

        foreach (var client in new[] { _http, _gateway, _targets })
        {
            client?.Dispose();
        }

        (_http, _gateway, _targets) = (null, null, null);
    }

    private static string KeyPath(HostContext context) =>
        string.IsNullOrWhiteSpace(context.Settings.Verifier.SignerKeyPath) ? context.Paths.MetricSignerKey : context.Settings.Verifier.SignerKeyPath;

    private static bool IsLocalDev(HostContext context, NetworkInfo network) =>
        context.Settings.Network.Mode == NetworkMode.LocalDev && network.ChainId == Symposia.Network.Node.Staking.DevFunding.LocalDevChainId;
}
