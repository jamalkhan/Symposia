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

    /// <summary>Smallest allowed multipart part except the last (S3: 5 MiB).</summary>
    public long MultipartMinPartSizeBytes { get; set; } = 5L * 1024 * 1024;

    /// <summary>How long an in-progress multipart upload lives without activity (refreshed by each part).</summary>
    public int MultipartUploadTtlDays { get; set; } = 7;

    public TimeSpan MultipartUploadTtl => TimeSpan.FromDays(MultipartUploadTtlDays);

    /// <summary>
    /// How long a Complete may hold an upload in Completing before the lease is released (a gateway that
    /// crashed mid-assembly). Must exceed the longest expected assembly.
    /// </summary>
    public int MultipartCompletingLeaseMinutes { get; set; } = 60;

    public TimeSpan CompletingLease => TimeSpan.FromMinutes(MultipartCompletingLeaseMinutes);

    /// <summary>How long finished (completed/aborted) upload rows are kept for idempotent Complete retries.</summary>
    public int MultipartRetentionDays { get; set; } = 7;

    /// <summary>How often expired uploads and stale Completing leases are reaped.</summary>
    public int MultipartReapIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Minimum age of a queued blob deletion before GC acts on it, so a concurrent write of the same
    /// bytes (dedup) can commit its reference first and cancel the deletion.
    /// </summary>
    public int GcMinAgeSeconds { get; set; } = 900;

    /// <summary>Port the S3 listener binds on all interfaces.</summary>
    public int ListenPort { get; set; } = 5181;

    /// <summary>Per-object operation history (#204).</summary>
    public HistoryOptions History { get; set; } = new();

    /// <summary>
    /// IPs or CIDRs of reverse proxies in front of the gateway (#204). Their <c>X-Forwarded-For</c> is
    /// honored for the client IP recorded in operation history; empty = trust nothing.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>How often every storage node is probed for health (seconds).</summary>
    public int ProbeIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Where storage nodes come from: <c>Static</c> (the <see cref="Nodes"/> list plus nodes added
    /// through /admin/nodes) or <c>Registry</c> (the on-chain NodeRegistry, read through the
    /// Blockchain Gateway at <see cref="BlockchainGatewayUrl"/>; issue #135).
    /// </summary>
    public NodeSourceKind NodeSource { get; set; } = NodeSourceKind.Static;

    /// <summary>Blockchain Gateway base URL; required when <see cref="NodeSource"/> is Registry.</summary>
    public string BlockchainGatewayUrl { get; set; } = "";

    /// <summary>
    /// The gateway host's node identity key (issue #140). When set (registry mode), every storage RPC
    /// is signed with it; storage nodes requiring caller auth only accept registered callers, so the
    /// host must be registered on <see cref="ChainId"/> / <see cref="NodeRegistryAddress"/>.
    /// </summary>
    public string NodeIdentityKeyPath { get; set; } = "";

    public ulong ChainId { get; set; }

    public string NodeRegistryAddress { get; set; } = "";

    /// <summary>How often the registry is re-read for storage nodes (seconds).</summary>
    public int RegistryRefreshSeconds { get; set; } = 10;
}

public enum NodeSourceKind
{
    Static,
    Registry,
}

public sealed class HistoryOptions
{
    /// <summary>History database file; empty = <c>gateway-history.db</c> beside the metadata DB.</summary>
    public string DbPath { get; set; } = "";

    /// <summary>Retention override in hours (capped at 720); null = 1 h on chain 31337, else 30 days.</summary>
    public int? RetentionHours { get; set; }

    public int QueueCapacity { get; set; } = 10_000;

    public int SweepIntervalSeconds { get; set; } = 300;
}
