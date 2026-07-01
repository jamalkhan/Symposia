namespace Symposia.BlobStorage.StorageNode;

public sealed class StorageNodeOptions
{
    public string StorageRoot { get; set; } = "data/blobs";

    public string ManifestDbPath { get; set; } = "data/manifest.db";

    public string NodeIdentityKeyPath { get; set; } = "data/node-identity.pem";

    /// <summary>Total bytes this node offers to the network (operator-configured).</summary>
    public long MaxCapacityBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>How often the node verifies all stored blob hashes (seconds). Default 3600 = 1 hour.</summary>
    public int IntegrityCheckIntervalSeconds { get; set; } = 3600;
}
