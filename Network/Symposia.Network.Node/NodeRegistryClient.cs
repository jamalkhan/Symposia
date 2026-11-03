using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Symposia.Network.Node;

public sealed record NodeRegistryStatus(bool Registered, string Endpoint, ulong Nonce);

public sealed record RegisteredNode(string Node, string Endpoint);

/// <summary>Registry reads/writes a node needs, abstracted for discovery tests.</summary>
public interface INodeRegistryClient
{
    Task<NodeRegistryStatus> GetStatusAsync(string address, CancellationToken ct = default);

    Task<IReadOnlyList<RegisteredNode>> ListNodesAsync(CancellationToken ct = default);

    Task RegisterAsync(string address, string endpoint, ulong nonce, byte[] signature, CancellationToken ct = default);
}

/// <summary>
/// Talks to the Bootstrap Chain Gateway's node-registry surface (issues #110,
/// #129). The Gateway is the single chain boundary: it relays the
/// already-signed registration and pays gas, and proxies the registry's view
/// functions. This client never handles or transmits the private key, only
/// resulting signatures.
/// </summary>
public sealed class NodeRegistryClient(HttpClient http) : INodeRegistryClient
{
    public async Task<NodeRegistryStatus> GetStatusAsync(string address, CancellationToken ct = default)
    {
        var status = await http.GetFromJsonAsync<StatusResponse>($"/v1/nodes/{address}", ct);
        return new NodeRegistryStatus(status?.Registered ?? false, status?.Endpoint ?? "", status?.Nonce ?? 0);
    }

    public async Task<IReadOnlyList<RegisteredNode>> ListNodesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<RegisteredNode>>("/v1/nodes", ct) ?? [];

    /// <summary>
    /// Submits a signed registration or endpoint update. Idempotent: the
    /// gateway/contract treat a repeat with an unchanged endpoint as a no-op.
    /// </summary>
    public async Task RegisterAsync(string address, string endpoint, ulong nonce, byte[] signature, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("/v1/nodes/register",
            new RegisterRequest(address, endpoint, nonce, "0x" + Convert.ToHexString(signature)), ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed record RegisterRequest(
        [property: JsonPropertyName("node")] string Node,
        [property: JsonPropertyName("endpoint")] string Endpoint,
        [property: JsonPropertyName("nonce")] ulong Nonce,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record StatusResponse(
        [property: JsonPropertyName("registered")] bool Registered,
        [property: JsonPropertyName("endpoint")] string? Endpoint,
        [property: JsonPropertyName("nonce")] ulong Nonce);
}
