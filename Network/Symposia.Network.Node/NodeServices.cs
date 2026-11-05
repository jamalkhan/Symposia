using System.Text.Json.Serialization;

namespace Symposia.Network.Node;

/// <summary>
/// What a node serves beyond its registered endpoint (issue #135), published at
/// <see cref="Path"/> on that endpoint. The registry holds one endpoint per node; this
/// document says where that node's other services (today: the storage gRPC API) live and,
/// since #145, which protocol version and features it speaks. It is a hint, not proof:
/// callers must verify identity on the service itself.
/// </summary>
/// <remarks>
/// Consumers ignore unknown fields and unknown feature names. A document without
/// <see cref="ProtocolVersion"/> comes from a node older than #145 and reads as version 0.
/// The JSON shape is pinned by <c>protocol/schemas/node-services.schema.json</c> (#146): fields
/// may be added, never removed or retyped.
/// </remarks>
public sealed record NodeServices(
    string Node,
    NodeServices.StorageService? Storage,
    int ProtocolVersion = 0,
    IReadOnlyList<string>? Features = null)
{
    public const string Path = "/v1/node/services";

    public bool Has(string feature) => Features?.Contains(feature, StringComparer.Ordinal) == true;

    /// <param name="Grpc">Base URL of the cleartext storage gRPC API; null when the node serves only TLS.</param>
    /// <param name="TlsFingerprint">Hex SHA-256 of the node's TLS certificate; callers pin it.</param>
    /// <param name="TlsProof">The node key's signature over the fingerprint and expiry (<see cref="StorageTlsProof"/>).</param>
    /// <param name="TlsNotAfter">The certificate's expiry (Unix seconds), covered by the proof.</param>
    /// <param name="GrpcTls">Base URL of the TLS storage gRPC API (<c>https://</c>, #142/#145).</param>
    public sealed record StorageService(
        string? Grpc,
        string? TlsFingerprint = null,
        string? TlsProof = null,
        long? TlsNotAfter = null,
        string? GrpcTls = null)
    {
        /// <summary>
        /// The TLS URL. Hosts running the #142 pre-release advertised TLS in <see cref="Grpc"/>; read
        /// that too until <see cref="ProtocolFeatures.StorageGrpcPlaintext"/> is retired.
        /// </summary>
        [JsonIgnore]
        public string? TlsUrl => GrpcTls ?? (IsHttps(Grpc) ? Grpc : null);

        /// <summary>The cleartext URL, if the node serves one.</summary>
        [JsonIgnore]
        public string? PlaintextUrl => IsHttps(Grpc) ? null : Grpc;

        private static bool IsHttps(string? url) => url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
    }

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
