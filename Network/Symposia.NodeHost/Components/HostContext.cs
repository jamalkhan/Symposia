using Microsoft.Extensions.Logging;
using Symposia.NodeHost.Diagnostics;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Components;

/// <summary>The chain this host's roles use, resolved from settings by the chain component.</summary>
public sealed record NetworkInfo(
    string GatewayUrl,
    ulong ChainId,
    string NodeRegistryAddress,
    string EpochRootRegistryAddress,
    string? RpcUrl,
    string? ProtocolActivationsAddress = null,
    string? RewardDistributorAddress = null,
    string? EpochControllerAddress = null,
    string? TelemetryRegistryAddress = null,
    string? StakingRegistryAddress = null,
    RpcSource RpcSource = RpcSource.Direct,
    ulong DeployBlock = 0,
    ulong? MaxLogBlockRange = null);

/// <summary>Where <see cref="NetworkInfo.RpcUrl"/> came from (issue #172), shown on the Network card.</summary>
public enum RpcSource
{
    /// <summary>This host's own chain or preset RPC.</summary>
    Direct,

    /// <summary>The joined gateway's read-only <c>/v1/rpc</c> proxy.</summary>
    Gateway,

    /// <summary>A public RPC the joined gateway's operator advertises.</summary>
    Advertised,
}

/// <summary>Shared state components read while starting.</summary>
public sealed class HostContext
{
    private readonly ILogger _hostLogger;
    private readonly object _stakingLock = new();
    private LocalDevStaking? _staking;

    public HostContext(HostPaths paths, SecretStore secrets, LogBuffer logs)
    {
        Paths = paths;
        Secrets = secrets;
        Logs = logs;
        Logging = new HostLogging(paths, () => Settings.Logging);
        _hostLogger = CreateLogger("host", "Symposia.NodeHost.Host");
        Alerts = new Alerts.OperatorAlerts(paths.AlertsState, CreateLogger("alerts", "Symposia.NodeHost.Alerts"));
    }

    public HostPaths Paths { get; }

    public SecretStore Secrets { get; }

    public LogBuffer Logs { get; }

    /// <summary>Operator Alerts every role raises into (issue #196).</summary>
    public Alerts.OperatorAlerts Alerts { get; }

    /// <summary>Per-service log levels and files (node / storage / compute).</summary>
    public HostLogging Logging { get; }

    /// <summary>Logger provider for one component; its service is chosen from the component name.</summary>
    public ILoggerProvider LoggerProvider(string component) => new ComponentLoggerProvider(component, Logs, Logging);

    public ILogger CreateLogger(string component, string category) => LoggerProvider(component).CreateLogger(category);

    /// <summary>Logger for host-level events (node log).</summary>
    public ILogger Logger => _hostLogger;

    public HostSettings Settings { get; set; } = new();

    /// <summary>Set by the chain component once the network is known; null until then.</summary>
    public NetworkInfo? Network { get; set; }

    /// <summary>Set when the LocalDev rewards stack was deployed with parameters it can't settle under (#181 FR4).</summary>
    public RewardsStaleNotice? RewardsStale { get; set; }

    /// <summary>Heartbeat acceptance, shared with the storage role (#181 FR5).</summary>
    public Symposia.Network.Node.Telemetry.TelemetryDiagnostics Telemetry { get; } = new();

    /// <summary>
    /// This host's LocalDev self-staking for the current network (#200), shared by every role; null
    /// when the network isn't LocalDev. A new network (switch or redeploy) gets a fresh one.
    /// </summary>
    public LocalDevStaking? LocalDevStaking
    {
        get
        {
            lock (_stakingLock)
            {
                if (Network is not { } network)
                {
                    return null;
                }

                if (_staking?.Network != network)
                {
                    _staking?.Dispose();
                    _staking = Components.LocalDevStaking.For(network, this);
                }

                return _staking;
            }
        }
    }

    /// <summary>Health summary served to peers by the role apps; set once the supervisor exists.</summary>
    public NodeHealth? Health { get; set; }

    public static string EndpointFor(string advertised, int port) =>
        string.IsNullOrWhiteSpace(advertised) ? $"http://127.0.0.1:{port}" : advertised;

    /// <summary>The single endpoint registered on-chain: storage's if enabled, else compute's (FR-D3).</summary>
    public string? PrimaryEndpoint => PrimaryEndpointFor(Settings);

    public static string? PrimaryEndpointFor(HostSettings settings) =>
        settings.Storage.Enabled ? EndpointFor(settings.Storage.AdvertisedEndpoint, settings.Storage.Port)
        : settings.Compute.Enabled ? EndpointFor(settings.Compute.AdvertisedEndpoint, settings.Compute.Port)
        : null;

    /// <summary>
    /// A role's data directory, scoped to the network it belongs to, so switching
    /// networks (or a local chain reset that redeploys the registry) never serves
    /// or counts data that belongs to a different network.
    /// </summary>
    public string RoleDataDir(string role)
    {
        var network = Network ?? throw new InvalidOperationException("Network not resolved yet.");
        var registry = network.NodeRegistryAddress.Length >= 10 ? network.NodeRegistryAddress[2..10].ToLowerInvariant() : "unknown";
        return Path.Combine(Paths.Component(role), $"{network.ChainId}-{registry}");
    }
}
