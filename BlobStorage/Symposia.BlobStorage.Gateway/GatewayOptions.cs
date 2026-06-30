using Symposia.BlobStorage.Gateway.Auth;

namespace Symposia.BlobStorage.Gateway;

public sealed class GatewayOptions
{
    /// <summary>S3 region reported in response headers and used in SigV4 credential scope.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>SQLite path for the gateway's derived metadata index.</summary>
    public string MetadataDbPath { get; set; } = "data/gateway-metadata.db";

    /// <summary>Minimum node confirmations before returning success to client.</summary>
    public int WriteQuorumCount { get; set; } = 1;

    /// <summary>gRPC endpoint URLs of storage nodes (e.g. "http://localhost:5180").</summary>
    public string[] Nodes { get; set; } = [];

    /// <summary>API credentials accepted by this gateway instance.</summary>
    public CredentialRecord[] Credentials { get; set; } = [];
}
