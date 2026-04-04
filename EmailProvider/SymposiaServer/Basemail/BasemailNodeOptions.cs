using System.Text.Json;

namespace NativeSmtpReceiver;

public sealed class BasemailNodeOptions
{
    public bool NetworkEnabled { get; init; }
    public bool RequireSignedRequests { get; init; }
    public string NodeId { get; init; } = "local-node";
    public string OperatorAddress { get; init; } = "0x0000000000000000000000000000000000000000";
    public string? MetadataUri { get; init; }
    public string? PublicKeyPem { get; init; }
    public string? PrivateKeyPem { get; init; }
    public int AdvertisedStorageGb { get; init; } = 100;
    public int AdvertisedBandwidthGbPerDay { get; init; } = 100;
    public int RegistrySyncSeconds { get; init; } = 30;
    public int RegistryInvalidationCooldownSeconds { get; init; } = 5;
    public int RegistryInvalidationDedupSeconds { get; init; } = 60;
    public int RegistryInvalidationFanout { get; init; } = 2;
    public int RegistryInvalidationMaxHops { get; init; } = 3;
    public IReadOnlyList<BasemailPeerRecord> Peers { get; init; } = Array.Empty<BasemailPeerRecord>();

    public bool HasSigningKeyPair =>
        !string.IsNullOrWhiteSpace(PublicKeyPem) &&
        !string.IsNullOrWhiteSpace(PrivateKeyPem);

    public static BasemailNodeOptions LoadFromEnvironment()
    {
        return new BasemailNodeOptions
        {
            NetworkEnabled = ResolveBool("BASEMAIL_NETWORK_ENABLED", false),
            RequireSignedRequests = ResolveBool("BASEMAIL_NETWORK_REQUIRE_SIGNATURES", false),
            NodeId = EmptyTo(Environment.GetEnvironmentVariable("BASEMAIL_NODE_ID"), "local-node"),
            OperatorAddress = EmptyTo(Environment.GetEnvironmentVariable("BASEMAIL_OPERATOR_ADDRESS"), "0x0000000000000000000000000000000000000000"),
            MetadataUri = EmptyToNull(Environment.GetEnvironmentVariable("BASEMAIL_NODE_METADATA_URI")),
            PublicKeyPem = EmptyToNull(Environment.GetEnvironmentVariable("BASEMAIL_NODE_PUBLIC_KEY_PEM")),
            PrivateKeyPem = EmptyToNull(Environment.GetEnvironmentVariable("BASEMAIL_NODE_PRIVATE_KEY_PEM")),
            AdvertisedStorageGb = ResolveInt("BASEMAIL_NODE_STORAGE_GB", 100),
            AdvertisedBandwidthGbPerDay = ResolveInt("BASEMAIL_NODE_BANDWIDTH_GB_PER_DAY", 100),
            RegistrySyncSeconds = ResolveInt("BASEMAIL_REGISTRY_SYNC_SECONDS", 30),
            RegistryInvalidationCooldownSeconds = ResolveInt("BASEMAIL_REGISTRY_INVALIDATION_COOLDOWN_SECONDS", 5),
            RegistryInvalidationDedupSeconds = ResolveInt("BASEMAIL_REGISTRY_INVALIDATION_DEDUP_SECONDS", 60),
            RegistryInvalidationFanout = ResolveInt("BASEMAIL_REGISTRY_INVALIDATION_FANOUT", 2),
            RegistryInvalidationMaxHops = ResolveInt("BASEMAIL_REGISTRY_INVALIDATION_MAX_HOPS", 3),
            Peers = LoadPeers()
        };
    }

    private static IReadOnlyList<BasemailPeerRecord> LoadPeers()
    {
        var configuredPath = Environment.GetEnvironmentVariable("BASEMAIL_NETWORK_PEERS_CONFIG");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Array.Empty<BasemailPeerRecord>();
        }

        var resolvedPath = PathResolution.ResolvePath(configuredPath);
        if (!File.Exists(resolvedPath))
        {
            return Array.Empty<BasemailPeerRecord>();
        }

        using var stream = File.OpenRead(resolvedPath);
        var peers = JsonSerializer.Deserialize<List<BasemailPeerRecord>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return peers?
            .Where(static peer => !string.IsNullOrWhiteSpace(peer.NodeId) && !string.IsNullOrWhiteSpace(peer.PublicKeyPem))
            .ToArray()
            ?? Array.Empty<BasemailPeerRecord>();
    }

    private static bool ResolveBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Equals("1", StringComparison.OrdinalIgnoreCase)
              || value.Equals("true", StringComparison.OrdinalIgnoreCase)
              || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : fallback;
    }

    private static string EmptyTo(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed record BasemailPeerRecord(
    string NodeId,
    string? BaseUrl,
    string PublicKeyPem,
    string? KeyId);
