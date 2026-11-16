namespace Symposia.BlobStorage.StorageNode;

public sealed class StorageNodeOptions
{
    public string StorageRoot { get; set; } = "data/blobs";

    public string ManifestDbPath { get; set; } = "data/manifest.db";

    /// <summary>Port the node's HTTP/gRPC listener binds on all interfaces.</summary>
    public int ListenPort { get; set; } = 5180;

    /// <summary>
    /// Dedicated HTTP/2-only (h2c) cleartext port for the gRPC storage API (issue #135), advertised
    /// as <c>storage.grpc.plaintext</c>. Kestrel won't negotiate HTTP/2 on a cleartext HTTP/1.1+2
    /// listener, so gRPC clients can't reach the shared port without TLS. 0 = no cleartext gRPC port;
    /// with <see cref="GrpcTlsPort"/> also 0, the legacy single listener on <see cref="ListenPort"/>.
    /// </summary>
    public int GrpcPort { get; set; }

    /// <summary>
    /// Require a node-identity token from a registered network member on every gRPC call (issue #140).
    /// Off for standalone/dev use; the Node Host turns it on.
    /// </summary>
    public bool RequireCallerAuth { get; set; }

    /// <summary>
    /// Port serving the gRPC storage API over TLS with a node-key-bound certificate (issue #142),
    /// advertised as <c>storage.grpc.tls</c>. 0 = no TLS. During the overlap (#145) a node serves
    /// both this and <see cref="GrpcPort"/>, so gateways from the previous release still reach it.
    /// </summary>
    public int GrpcTlsPort { get; set; }

    /// <summary>Where the gRPC TLS certificate lives; empty = grpc-tls.pfx next to the manifest database.</summary>
    public string TlsCertificatePath { get; set; } = "";

    /// <summary>Total bytes this node offers to the network (operator-configured).</summary>
    public long MaxCapacityBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>How often the node verifies all stored blob hashes (seconds). Default 3600 = 1 hour.</summary>
    public int IntegrityCheckIntervalSeconds { get; set; } = 3600;

    /// <summary>Operator bearer token for <c>GET /metrics</c> (#1 FR-26); empty = every scrape gets 401.</summary>
    public string MetricsBearerToken { get; set; } = "";

    /// <summary>The region this node serves (e.g. <c>us-east</c>), reported in probes and <c>/ready</c>.</summary>
    public string Region { get; set; } = "";

    /// <summary>
    /// Whether the node's region has been verified (Requirements/Network/region-identification-and-verification.md).
    /// Until that verification exists the operator attests it here.
    /// </summary>
    public bool RegionVerified { get; set; }

    /// <summary>Report not ready until the region is verified (#1 FR-25). Off for standalone/dev use.</summary>
    public bool RequireRegionVerification { get; set; }

    // Fault-domain inputs reported in probes (#8 FR2.1). Self-declared until region verification exists.

    /// <summary>This node's public IP as the operator knows it; gateways prefer the address they connect to.</summary>
    public string PublicIp { get; set; } = "";

    /// <summary>The node's autonomous system number (ISP / hosting provider); 0 = unknown.</summary>
    public long Asn { get; set; }

    /// <summary>The data center or colocation facility the node is in, if any.</summary>
    public string FacilityId { get; set; } = "";

    /// <summary>Upload capacity for the replication bandwidth budget (#8 FR3.3); 0 = unknown, the gateway assumes a default.</summary>
    public long UploadBytesPerSecond { get; set; }

    /// <summary>Performance tier reported in <c>/ready</c> (Requirements/BlobStorage/performance-tiers-and-workload-routing.md).</summary>
    public int Tier { get; set; }

    /// <summary>How long <c>/ready</c> reports draining before listeners close (#1 FR-28).</summary>
    public int DrainNoticeSeconds { get; set; }

    /// <summary>How long in-flight calls may run after shutdown starts (#1 FR-28).</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Manifest change-feed events kept for WatchManifest (#12). A gateway further behind than this resyncs
    /// from a snapshot, so this bounds disk, not correctness.
    /// </summary>
    public long ManifestEventRetention { get; set; } = 100_000;

    /// <summary>How often WatchManifest sends a heartbeat with the node's digest when nothing changes (#12).</summary>
    public int ManifestHeartbeatSeconds { get; set; } = 15;

    /// <summary>Epoch Merkle root submission (#12 Layer 3).</summary>
    public EpochRootOptions EpochRoots { get; set; } = new();
}

/// <summary>
/// Where a storage node attests its manifest on-chain (#12 Layer 3). Epochs are the network's telemetry epochs
/// (<c>StorageNode:Telemetry</c>, synced from the EpochController when the telemetry chain is configured), so roots,
/// heartbeats and rewards all count the same epoch.
/// </summary>
public sealed class EpochRootOptions
{
    /// <summary>Submit a root at each epoch boundary. Needs the Blockchain Gateway and <see cref="RegistryAddress"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The EpochRootRegistry contract the root's EIP-712 signature is bound to.</summary>
    public string RegistryAddress { get; set; } = "";

    /// <summary>How often the submitter checks for a finished epoch and retries an unconfirmed submission.</summary>
    public int CheckIntervalSeconds { get; set; } = 30;
}
