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

    /// <summary>
    /// How far a storage node's clock may run ahead before its fresh TLS certificate counts as not yet valid (#252).
    /// Expiry has no tolerance: it is the replay boundary the node's proof signs.
    /// </summary>
    public int StorageTlsClockSkewSeconds { get; set; } = 300;

    public TimeSpan StorageTlsClockSkew => TimeSpan.FromSeconds(Math.Max(0, StorageTlsClockSkewSeconds));

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
    /// A bucket's multipart TTL (#16 FR1.1): its lifecycle's hours if set, else the gateway default, clamped to
    /// 1 hour – 30 days.
    /// </summary>
    public TimeSpan MultipartTtlFor(int? bucketHours) =>
        TimeSpan.FromHours(Math.Clamp(bucketHours ?? MultipartUploadTtlDays * 24, 1, 30 * 24));

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

    /// <summary>Per-epoch factor reports for Blob Gateway rewards (#221); LocalDev only for now.</summary>
    public Rewards.ServiceMetricsOptions ServiceMetrics { get; set; } = new();

    /// <summary>
    /// Operator bearer token for <c>GET /metrics</c> (#1 FR-26). Not a tenant credential. Empty = every
    /// scrape is refused with 401.
    /// </summary>
    public string MetricsBearerToken { get; set; } = "";

    /// <summary>
    /// How long <c>/ready</c> reports draining before the server stops accepting connections (#1 FR-28),
    /// so the load balancer takes this instance out of rotation first. In-flight requests then get
    /// <see cref="ShutdownTimeoutSeconds"/> to finish.
    /// </summary>
    public int DrainNoticeSeconds { get; set; }

    /// <summary>How long in-flight requests may run after shutdown starts before the host stops (#1 FR-28).</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Per-tenant storage quotas in bytes (#1 FR-2): a write that would take the tenant past its quota is
    /// refused with 507 before any node is contacted. Tenants not listed are unlimited.
    /// </summary>
    public Dictionary<string, long> TenantQuotaBytes { get; set; } = [];

    /// <summary>Copy counts, write quorum and placement (#7, #8).</summary>
    public RedundancyOptions Redundancy { get; set; } = new();

    /// <summary>The Credential Authority this gateway caches credentials from (#3); empty Url = configured credentials only.</summary>
    public CredentialAuthorityOptions CredentialAuthority { get; set; } = new();

    /// <summary>Garbage collection (#16).</summary>
    public GcOptions Gc { get; set; } = new();

    /// <summary>The gossip-fed CID routing cache and its fallbacks (#12 Layer 2/3).</summary>
    public RoutingOptions Routing { get; set; } = new();
}

public enum RedundancyMode
{
    /// <summary>
    /// Small clusters (single-node dev, a network not yet spanning regions): every writable node is a
    /// target, <see cref="GatewayOptions.WriteQuorumCount"/> acks are a quorum, regions aren't enforced.
    /// </summary>
    Bootstrap,

    /// <summary>The full region tables of #7/#8: 4/5/6/7/2N copies, region-aware quorum, fault domains.</summary>
    Network,
}

public sealed class RedundancyOptions
{
    public RedundancyMode Mode { get; set; } = RedundancyMode.Bootstrap;

    /// <summary>
    /// Longest wait for a write's quorum once its body has been sent (#7 FR4); past it the write fails with
    /// 503 and its copies are discarded. Measured from the end of the body, so a slow upload isn't cut short.
    /// </summary>
    public int WriteQuorumTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Per-tenant overrides of <see cref="WriteQuorumTimeoutSeconds"/> for enterprise large-blob or
    /// slow-network needs (#7 FR12). Operators set these on request; see docs/blob-storage-operations.md.
    /// </summary>
    public Dictionary<string, int> TenantWriteQuorumTimeoutSeconds { get; set; } = [];

    /// <summary>How long the copies past quorum may take to land before repair takes over (#7 FR5).</summary>
    public int RemainderTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Region of storage nodes (by URL or node ID) that don't report one in their probe. A probed region
    /// wins; nodes with neither count as "no region" (overflow only). A list, not a map: URLs contain
    /// the configuration key separator.
    /// </summary>
    public NodeRegion[] NodeRegions { get; set; } = [];

    /// <summary>
    /// Share of a node's upload capacity background replication may use (#8 FR3.3, default 25 %).
    /// A governance parameter; set here until governance wiring exists.
    /// </summary>
    public int BandwidthBudgetPercent { get; set; } = 25;

    /// <summary>The share for nodes in penalty stage 3 or 4, whose data is being evacuated (#8 FR3.3).</summary>
    public int RecoveryBandwidthBudgetPercent { get; set; } = 50;

    /// <summary>Upload capacity assumed for nodes that report none (bytes/s).</summary>
    public long DefaultNodeUploadBytesPerSecond { get; set; } = 100L * 1024 * 1024;

    /// <summary>Most copies P3 opportunistic redundancy may add up to (#8 FR8.1, governance parameter); 0 = never beyond target.</summary>
    public int MaxReplicationFactor { get; set; } = 8;

    /// <summary>How often a random replica gets a Merkle range challenge, on average (#8 FR6.4); 0 = off.</summary>
    public int ChallengeIntervalSeconds { get; set; } = 60;

    /// <summary>How long a challenged node has to answer before it counts as failed.</summary>
    public int ChallengeTimeoutSeconds { get; set; } = 10;

    public TimeSpan WriteQuorumTimeout(string tenantId) => TimeSpan.FromSeconds(Math.Max(1,
        TenantWriteQuorumTimeoutSeconds.TryGetValue(tenantId, out var seconds) ? seconds : WriteQuorumTimeoutSeconds));
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

public sealed class NodeRegion
{
    /// <summary>The node's URL or registry ID.</summary>
    public string Node { get; set; } = "";

    public string Region { get; set; } = "";
}

public sealed class GcOptions
{
    /// <summary>How long a deleted object stays recoverable before its bytes are purged (#16 FR2.1); buckets may set up to 365.</summary>
    public int RecoveryDays { get; set; } = 30;

    /// <summary>How long an unreferenced blob found on a node waits before it can be deleted (#16 FR5.2).</summary>
    public int OrphanGraceHours { get; set; } = 48;

    /// <summary>Most deletions GC sends a node per second, so it never competes with client I/O (#16 FR8.1).</summary>
    public int DeletesPerSecondPerNode { get; set; } = 20;

    /// <summary>How often the GC evaluators look for newly eligible garbage (seconds).</summary>
    public int EvaluateIntervalSeconds { get; set; } = 300;

    /// <summary>Deletions per GC batch kept for the low tier (#16 FR8.3 starvation guard).</summary>
    public int LowPriorityReserve { get; set; } = 5;
}

public sealed class CredentialAuthorityOptions
{
    /// <summary>How far the Authority's clock may run ahead before its fresh certificate counts as not yet valid (#252).</summary>
    public int ClockSkewSeconds { get; set; } = 300;

    public TimeSpan ClockSkew => TimeSpan.FromSeconds(Math.Max(0, ClockSkewSeconds));

    /// <summary>Base URL of the Credential Authority (HTTP/2 for Watch).</summary>
    public string Url { get; set; } = "";

    /// <summary>The service token the Authority accepts from gateways.</summary>
    public string ServiceToken { get; set; } = "";

    /// <summary>
    /// Fallback poll interval (#3 FR5.18): bounds propagation even with the Watch stream down. Pulls deltas since
    /// #245. Keep it at most half the 60 s revocation SLA.
    /// </summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>
    /// The full-snapshot reconcile interval (#245 FR4c), jittered ±20 %. Polls and Watch pushes pull deltas;
    /// this pulls everything, healing anything a delta could have missed.
    /// </summary>
    public double ReconcileMinutes { get; set; } = 10;

    // Mutual TLS with the Authority (#245 FR6). Empty = no client certificate (the Authority's Off/Permissive modes).

    /// <summary>This gateway's client certificate: PFX, or PEM (key in the file or <see cref="ClientKeyPath"/>).</summary>
    public string ClientCertificatePath { get; set; } = "";

    public string ClientKeyPath { get; set; } = "";

    public string ClientCertificatePassword { get; set; } = "";

    /// <summary>
    /// Pinned SHA-256 fingerprints of the Authority's server certificate (several, so it can rotate with overlap).
    /// Pinning replaces CA validation, like storage-node TLS (#142).
    /// </summary>
    public string[] ServerFingerprints { get; set; } = [];

    /// <summary>Alternatively, a CA (PEM) the Authority's certificate must chain to, with normal name checks.</summary>
    public string ServerCaPath { get; set; } = "";

    /// <summary>This gateway's ID in audit events; empty = the machine name.</summary>
    public string GatewayId { get; set; } = "";

    /// <summary>Most use events kept locally while the Authority is unreachable before the oldest are dropped (with an error).</summary>
    public int AuditSpoolLimit { get; set; } = 1_000_000;
}

/// <summary>
/// How the gateway finds replicas beyond its own metadata index (#12). Each storage node's manifest change feed
/// keeps an in-memory CID → holders cache; on a miss the gateway asks its peer gateways, then checks nodes'
/// inclusion proofs against their on-chain epoch roots.
/// </summary>
public sealed class RoutingOptions
{
    /// <summary>Subscribe to storage nodes' manifest change feeds (WatchManifest).</summary>
    public bool WatchManifests { get; set; } = true;

    /// <summary>Peer gateways' base URLs (e.g. <c>http://gw2:5181</c>) asked to resolve a CID this gateway can't (FR2.6).</summary>
    public string[] PeerGateways { get; set; } = [];

    /// <summary>
    /// Bearer token peer gateways present to <c>/internal/v1/routing</c>, and this gateway presents to them.
    /// Empty = the endpoint refuses every call (it is never open).
    /// </summary>
    public string PeerToken { get; set; } = "";

    /// <summary>How long a peer gateway lookup may take before it's abandoned.</summary>
    public int PeerTimeoutMilliseconds { get; set; } = 2000;

    /// <summary>Ask nodes for inclusion proofs against their on-chain epoch roots when nothing else locates a CID (FR2.6, FR3.2).</summary>
    public bool ChainFallback { get; set; } = true;

    /// <summary>How many nodes the on-chain fallback asks for a proof at once.</summary>
    public int ChainFallbackParallelism { get; set; } = 8;
}
