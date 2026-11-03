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

    /// <summary>Writes a host-level line to the diagnostics log and console.</summary>
    public void Log(string level, string message)
    {
        Logs.Add(new LogLine(DateTimeOffset.UtcNow, "host", level, message));
        Console.WriteLine($"[host] {level}: {message}");
    }
}
