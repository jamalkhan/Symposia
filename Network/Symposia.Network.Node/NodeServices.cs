namespace Symposia.Network.Node;

/// <summary>
/// What a node serves beyond its registered endpoint (issue #135), published at
/// <see cref="Path"/> on that endpoint. The registry holds one endpoint per node; this
/// document says where that node's other services (today: the storage gRPC API) live.
/// It is a hint, not proof: callers must verify identity on the service itself.
/// </summary>
public sealed record NodeServices(string Node, NodeServices.StorageService? Storage)
{
    public const string Path = "/v1/node/services";

    /// <param name="Grpc">Base URL of the storage gRPC API (<c>https://</c> when TLS is on, #142).</param>
    /// <param name="TlsFingerprint">Hex SHA-256 of the node's TLS certificate; callers pin it.</param>
    /// <param name="TlsProof">The node key's signature over the fingerprint (<see cref="StorageTlsProof"/>).</param>
    public sealed record StorageService(string Grpc, string? TlsFingerprint = null, string? TlsProof = null);

    /// <summary>
    /// The gRPC base URL: the advertised endpoint's scheme and host with the gRPC port, so a
    /// node advertising its LAN address publishes a LAN gRPC address too. With no dedicated
    /// gRPC port the advertised endpoint itself (legacy shared listener) is returned.
    /// </summary>
    public static string GrpcUrl(string advertisedEndpoint, int listenPort, int grpcPort, bool tls = false)
    {
        var baseUrl = NodeEndpoint.Normalize(advertisedEndpoint) ?? $"http://127.0.0.1:{listenPort}";
        if (grpcPort <= 0)
        {
            return baseUrl;
        }

        var builder = new UriBuilder(baseUrl) { Port = grpcPort, Scheme = tls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }
}
