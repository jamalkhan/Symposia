using Grpc.Net.Client;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// A live gRPC connection to one storage node. The gateway maintains one channel per
/// configured node and reuses it across requests. Channels are thread-safe.
/// </summary>
public sealed class NodeConnection : IDisposable
{
    private readonly GrpcChannel _channel;

    public NodeConnection(string url)
    {
        Url = url;
        // Allow unencrypted HTTP/2 (h2c) for node communication in dev.
        // Production: set TLS on the channel or terminate TLS at a sidecar.
        _channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
        {
            HttpHandler = new HttpClientHandler(),
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

    public Protocol.StorageNode.StorageNodeClient Client { get; }

    /// <summary>Last successful probe response. Null if never probed or probe failed.</summary>
    public Protocol.ProbeResponse? LastProbe { get; set; }

    /// <summary>Time of the last successful probe.</summary>
    public DateTimeOffset LastProbeTime { get; set; } = DateTimeOffset.MinValue;

    /// <summary>A node is considered healthy if it responded to a probe in the last 90 seconds.</summary>
    public bool IsHealthy =>
        LastProbe?.Healthy == true &&
        (DateTimeOffset.UtcNow - LastProbeTime).TotalSeconds < 90;

    public void Dispose() => _channel.Dispose();
}
