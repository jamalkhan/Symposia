namespace Symposia.Network.Node;

/// <summary>
/// Node identity, registration, and peer discovery configuration shared by
/// every node type (issue #129). Bound from the hosting node's own config
/// section (e.g. <c>StorageNode</c>, <c>ComputeNode</c>), so existing keys
/// such as <c>NodeIdentityKeyPath</c> keep working unchanged.
/// </summary>
public sealed class NodeNetworkOptions
{
    public string NodeIdentityKeyPath { get; set; } = "data/node-identity.key";

    /// <summary>
    /// Permits generating a fresh identity when this node was previously
    /// initialized but its key is missing, corrupt, or in a legacy format
    /// (#129 FR-A3). Never replaces a valid key.
    /// </summary>
    public bool ResetIdentity { get; set; }

    /// <summary>
    /// Whether this host registers its endpoint on-chain. The single Node Host
    /// (issue #133) turns this off in each role and registers one primary
    /// endpoint itself, so only one writer ever uses the registry nonce.
    /// </summary>
    public bool EnableRegistration { get; set; } = true;

    /// <summary>Whether this host runs peer discovery (turned off in Node Host roles; the host discovers once).</summary>
    public bool EnableDiscovery { get; set; } = true;

    /// <summary>Base URL of the Bootstrap Chain Gateway (issue #110). Empty disables registration and discovery.</summary>
    public string BlockchainGatewayUrl { get; set; } = "";

    /// <summary>Deployed NodeRegistry contract address, used to scope EIP-712 and handshake signatures.</summary>
    public string NodeRegistryAddress { get; set; } = "";

    /// <summary>Chain ID of the bootstrap chain, used to scope EIP-712 and handshake signatures.</summary>
    public ulong ChainId { get; set; } = 31337;

    /// <summary>Base URL other nodes use to reach this node (e.g. <c>http://10.0.0.5:5180</c>). Empty skips registration.</summary>
    public string AdvertisedEndpoint { get; set; } = "";

    /// <summary>Optional extra endpoints to handshake with, in addition to registry members (#129 FR-C2).</summary>
    public string[] SeedPeers { get; set; } = [];

    public int DiscoveryIntervalSeconds { get; set; } = 60;

    public int HandshakeTimeoutSeconds { get; set; } = 5;

    /// <summary>Upper bound on concurrent outbound handshakes per discovery cycle.</summary>
    public int MaxConcurrentHandshakes { get; set; } = 8;

    /// <summary>Max handshake requests accepted per remote IP per minute.</summary>
    public int HandshakeRateLimitPerMinute { get; set; } = 60;
}
