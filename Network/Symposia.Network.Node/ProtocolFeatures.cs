using System.Text.Json;
using System.Text.Json.Serialization;

namespace Symposia.Network.Node;

/// <summary>
/// Protocol features a node can advertise in <see cref="NodeServices.Features"/> (issue #145).
/// The names and their lifecycle live in <c>protocol/features.json</c>, embedded here as
/// <see cref="Registry"/>; code refers to features only through these constants. A test keeps
/// the two in step, and CI checks registry changes against the compatibility rules (#146).
/// </summary>
public static class ProtocolFeatures
{
    public const string StorageGrpcPlaintext = "storage.grpc.plaintext";

    public const string StorageGrpcTls = "storage.grpc.tls";

    public const string StorageRpcAuthV1 = "storage.rpc.auth.v1";

    private static readonly Lazy<ProtocolRegistry> LazyRegistry = new(Load);

    /// <summary>The embedded <c>protocol/features.json</c>.</summary>
    public static ProtocolRegistry Registry => LazyRegistry.Value;

    /// <summary>The protocol version this build speaks.</summary>
    public static int ProtocolVersion => Registry.ProtocolVersion;

    private static ProtocolRegistry Load()
    {
        using var stream = typeof(ProtocolFeatures).Assembly.GetManifestResourceStream("Symposia.Protocol.features.json")
            ?? throw new InvalidOperationException("protocol/features.json is not embedded in Symposia.Network.Node.");
        return JsonSerializer.Deserialize<ProtocolRegistry>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("protocol/features.json is empty.");
    }
}

/// <summary>Lifecycle stages: offered → required → retired (docs/engineering/protocol-compatibility.md).</summary>
public static class FeatureStage
{
    public const string Offered = "offered";

    public const string Required = "required";

    public const string Retired = "retired";
}

public sealed record ProtocolRegistry(string Release, int ProtocolVersion, IReadOnlyList<ProtocolFeature> Features);

public sealed record ProtocolFeature(
    string Name,
    string Description,
    int? Issue,
    string? Replaces,
    IReadOnlyList<ProtocolFeatureStage> History)
{
    [JsonIgnore]
    public string Stage => History.Count > 0 ? History[^1].Stage : FeatureStage.Offered;
}

public sealed record ProtocolFeatureStage(string Stage, string Release);
