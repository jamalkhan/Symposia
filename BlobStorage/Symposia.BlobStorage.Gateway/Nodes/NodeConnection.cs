using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// A live gRPC connection to one storage node. The gateway maintains one channel per
/// configured node and reuses it across requests. Channels are thread-safe.
/// </summary>
public sealed class NodeConnection : IDisposable
{
    private readonly GrpcChannel _channel;

    public NodeConnection(string url, string? expectedNodeId = null, IStorageRpcSigner? signer = null, string? tlsFingerprint = null)
    {
        TlsFingerprint = tlsFingerprint;
        Url = url;
        ExpectedNodeId = expectedNodeId;
        Id = expectedNodeId ?? url;
        // Allow unencrypted HTTP/2 (h2c) for node communication in dev.
        // Production: set TLS on the channel or terminate TLS at a sidecar.
        // A short connect timeout keeps a powered-off node from stalling writes and probes.
        _channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                EnableMultipleHttp2Connections = true,
                // TLS (#142): trust exactly the certificate whose fingerprint the node's key vouched for.
                // Pinning replaces CA validation, so chain/name errors are expected and ignored.
                SslOptions = tlsFingerprint is null ? new() : new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => PinMatches(certificate?.GetRawCertData(), tlsFingerprint),
                },
            },
        });
        // Registry nodes authenticate callers (issue #140): sign every call for this node's address.
        Client = signer is not null && expectedNodeId is not null
            ? new Protocol.StorageNode.StorageNodeClient(_channel.Intercept(new StorageRpcSigningInterceptor(signer, expectedNodeId)))
            : new Protocol.StorageNode.StorageNodeClient(_channel);
    }

    // For integration tests: inject a pre-built client backed by an in-process TestServer.
    internal NodeConnection(string url, HttpClient httpClient)
    {
        Url = url;
        Id = url;
        _channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = httpClient,
        });
        Client = new Protocol.StorageNode.StorageNodeClient(_channel);
    }

    public string Url { get; }

    /// <summary>The pinned TLS certificate fingerprint, or null for a cleartext (h2c) node.</summary>
    public string? TlsFingerprint { get; }

    /// <summary>True when the presented certificate (DER) hashes to the pinned fingerprint (#142).</summary>
    public static bool PinMatches(byte[]? certificateDer, string pinnedFingerprint) =>
        certificateDer is not null && string.Equals(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(certificateDer)), pinnedFingerprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The node's identity in replica metadata (<c>node_ids</c>, GC queue, repair tasks; issue #137):
    /// its registry address when it came from the on-chain registry, otherwise its URL. Stable
    /// across address changes; the URL is just where to reach it right now.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The node address the registry lists for this URL (issue #135). When set, a probe whose
    /// NodeId differs marks the node unhealthy, so an endpoint can't pose as another node.
    /// </summary>
    public string? ExpectedNodeId { get; }

    /// <summary>Why the last probe failed (e.g. "Unauthenticated: caller is not registered"), or null after a success.</summary>
    public string? LastProbeError { get; set; }

    /// <summary>When this connection was created (start of the gateway's knowledge of the node).</summary>
    public DateTimeOffset KnownSince { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether copies on this node still count toward an object's healthy replicas: it's healthy,
    /// or it has been down for less than <paramref name="grace"/> (issue #137). Past that, the
    /// compliance scan stops counting it and repair adds a copy elsewhere.
    /// </summary>
    public bool CountsAsReplica(TimeSpan grace)
    {
        if (IsHealthy)
        {
            return true;
        }

        var downSince = LastUnhealthyAt == DateTimeOffset.MinValue ? KnownSince : LastUnhealthyAt;
        return DateTimeOffset.UtcNow - downSince < grace;
    }

    /// <summary>When the node's source last listed it; nodes absent past the grace period are dropped.</summary>
    public DateTimeOffset LastSeenInSource { get; set; } = DateTimeOffset.UtcNow;

    public Protocol.StorageNode.StorageNodeClient Client { get; }

    /// <summary>Last successful probe response. Null if never probed or probe failed.</summary>
    public Protocol.ProbeResponse? LastProbe { get; set; }

    /// <summary>Time of the last successful probe.</summary>
    public DateTimeOffset LastProbeTime { get; set; } = DateTimeOffset.MinValue;

    /// <summary>A node is considered healthy if it responded to a probe in the last 90 seconds.</summary>
    public bool IsHealthy =>
        LastProbe?.Healthy == true &&
        (DateTimeOffset.UtcNow - LastProbeTime).TotalSeconds < 90;

    /// <summary>
    /// Set to true by NodeRegistry when this node first comes back online after a failed probe.
    /// NodeReconciler reads and clears this flag to schedule reconciliation.
    /// </summary>
    public bool JustReconnected { get; set; }

    /// <summary>Time when the node was last observed to be unhealthy (or never). Used for grace-period checks.</summary>
    public DateTimeOffset LastUnhealthyAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Treat the node as down until its next successful probe (e.g. after a failed read).</summary>
    public void MarkUnhealthy()
    {
        if (IsHealthy)
        {
            LastUnhealthyAt = DateTimeOffset.UtcNow;
        }

        LastProbe = null;
        LastProbeTime = DateTimeOffset.MinValue;
    }

    public void Dispose() => _channel.Dispose();
}
