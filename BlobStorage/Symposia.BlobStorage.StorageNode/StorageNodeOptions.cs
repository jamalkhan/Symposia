namespace Symposia.BlobStorage.StorageNode;

public sealed class StorageNodeOptions
{
    public string StorageRoot { get; set; } = "data/blobs";

    public string ManifestDbPath { get; set; } = "data/manifest.db";

    public string NodeIdentityKeyPath { get; set; } = "data/node-identity.pem";

    /// <summary>Base URL of the Bootstrap Chain Gateway (issue #110) used for on-chain node registration. Empty disables registration.</summary>
    public string BlockchainGatewayUrl { get; set; } = "";

    /// <summary>Deployed NodeRegistry contract address, used to scope this node's EIP-712 registration signature.</summary>
    public string NodeRegistryAddress { get; set; } = "";

    /// <summary>Chain ID of the bootstrap chain, used to scope this node's EIP-712 registration signature.</summary>
    public ulong ChainId { get; set; } = 31337;

    /// <summary>Total bytes this node offers to the network (operator-configured).</summary>
    public long MaxCapacityBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>How often the node verifies all stored blob hashes (seconds). Default 3600 = 1 hour.</summary>
    public int IntegrityCheckIntervalSeconds { get; set; } = 3600;
}
