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

    /// <summary>
    /// Minimum number of healthy replicas every blob must have.
    /// The ReplicationMonitor triggers repair when any blob falls below this.
    /// Should be &lt;= WriteQuorumCount for writes to succeed during normal operation.
    /// Requirements/BlobStorage/redundancy-and-data-integrity.md — minimum 4 on a full network;
    /// defaults to 1 for single-node dev.
    /// </summary>
    public int MinCopiesPerObject { get; set; } = 1;

    /// <summary>gRPC endpoint URLs of storage nodes (e.g. "http://localhost:5180").</summary>
    public string[] Nodes { get; set; } = [];

    /// <summary>API credentials accepted by this gateway instance.</summary>
    public CredentialRecord[] Credentials { get; set; } = [];

    /// <summary>
    /// Secret required in the X-Admin-Secret request header to access /admin/* endpoints.
    /// Leave empty to disable admin endpoints entirely.
    /// </summary>
    public string AdminSecret { get; set; } = "";

    /// <summary>How often the GcWorker retries pending node deletions (seconds).</summary>
    public int GcRetryIntervalSeconds { get; set; } = 60;

    /// <summary>How often the ReplicationMonitor scans for under-replicated or corrupt objects (seconds).</summary>
    public int ReplicationCheckIntervalSeconds { get; set; } = 1800;

    /// <summary>
    /// Grace period before treating an offline node as permanently gone (seconds).
    /// Requirements/BlobStorage/redundancy-and-data-integrity.md#grace-period-for-transient-outages
    /// </summary>
    public int NodeGracePeriodSeconds { get; set; } = 900;
}
