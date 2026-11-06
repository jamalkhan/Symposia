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
}
