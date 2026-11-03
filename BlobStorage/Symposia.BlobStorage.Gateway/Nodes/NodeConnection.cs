using Grpc.Net.Client;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// A live gRPC connection to one storage node. The gateway maintains one channel per
/// configured node and reuses it across requests. Channels are thread-safe.
/// </summary>
public sealed class NodeConnection : IDisposable
{
    private readonly GrpcChannel _channel;

    public NodeConnection(string url, string? expectedNodeId = null)
    {
        Url = url;
        ExpectedNodeId = expectedNodeId;
        // Allow unencrypted HTTP/2 (h2c) for node communication in dev.
        // Production: set TLS on the channel or terminate TLS at a sidecar.
        // A short connect timeout keeps a powered-off node from stalling writes and probes.
        _channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), EnableMultipleHttp2Connections = true },
        });
        Client = new Protocol.StorageNode.StorageNodeClient(_channel);
    }

    // For integration tests: inject a pre-built client backed by an in-process TestServer.
    internal NodeConnection(string url, HttpClient httpClient)
    {
        Url = url;
        _channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = httpClient,
        });
        Client = new Protocol.StorageNode.StorageNodeClient(_channel);
    }

    public string Url { get; }

    /// <summary>
    /// The node address the registry lists for this URL (issue #135). When set, a probe whose
    /// NodeId differs marks the node unhealthy, so an endpoint can't pose as another node.
    /// </summary>
    public string? ExpectedNodeId { get; set; }

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

    public void Dispose() => _channel.Dispose();
}
