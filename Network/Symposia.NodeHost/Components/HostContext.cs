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
public sealed class HostContext(HostPaths paths, SecretStore secrets, LogBuffer logs)
{
    public HostPaths Paths { get; } = paths;

    public SecretStore Secrets { get; } = secrets;

    public LogBuffer Logs { get; } = logs;

    public HostSettings Settings { get; set; } = new();

    /// <summary>Set by the chain component once the network is known; null until then.</summary>
    public NetworkInfo? Network { get; set; }

    public static string EndpointFor(string advertised, int port) =>
        string.IsNullOrWhiteSpace(advertised) ? $"http://127.0.0.1:{port}" : advertised;

    /// <summary>The single endpoint registered on-chain: storage's if enabled, else compute's (FR-D3).</summary>
    public string? PrimaryEndpoint =>
        Settings.Storage.Enabled ? EndpointFor(Settings.Storage.AdvertisedEndpoint, Settings.Storage.Port)
        : Settings.Compute.Enabled ? EndpointFor(Settings.Compute.AdvertisedEndpoint, Settings.Compute.Port)
        : null;
}
