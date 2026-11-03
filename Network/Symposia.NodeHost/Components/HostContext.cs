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
    string? RpcUrl);

/// <summary>Shared state components read while starting.</summary>
public sealed class HostContext
{
    private readonly ILogger _hostLogger;

    public HostContext(HostPaths paths, SecretStore secrets, LogBuffer logs)
    {
        Paths = paths;
        Secrets = secrets;
        Logs = logs;
        Logging = new HostLogging(paths, () => Settings.Logging);
        _hostLogger = CreateLogger("host", "Symposia.NodeHost.Host");
    }

    public HostPaths Paths { get; }

    public SecretStore Secrets { get; }

    public LogBuffer Logs { get; }

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
