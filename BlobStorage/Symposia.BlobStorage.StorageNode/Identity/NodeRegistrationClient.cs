using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Symposia.BlobStorage.StorageNode.Identity;

/// <summary>
/// Talks to the Bootstrap Chain Gateway's node-registration surface (issue
/// #110) to complete this node's cold-start step 1 ("generate keypair and
/// register on-chain", per Requirements/BlobStorage/metadata-architecture.md).
/// The gateway relays the already-signed payload and pays gas; this client
/// never handles or transmits the private key itself, only the resulting
/// signature (issue #109, Functional Requirement 2/3).
/// </summary>
public sealed class NodeRegistrationClient(HttpClient http)
{
    public async Task<bool> IsRegisteredAsync(string address, CancellationToken ct = default)
    {
        var status = await http.GetFromJsonAsync<RegistrationStatusResponse>($"/v1/nodes/{address}", ct);
        return status?.Registered ?? false;
    }

    /// <summary>
    /// Submits a registration request. Idempotent from the caller's point of
    /// view: the gateway/contract treat a repeat submission for an
    /// already-registered address as a safe no-op (issue #109, FR5), so this
    /// method can be called unconditionally on every cold start.
    /// </summary>
    public async Task RegisterAsync(string address, byte[] signature, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("/v1/nodes/register",
            new RegisterRequest(address, "0x" + Convert.ToHexString(signature)), ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed record RegisterRequest(
        [property: JsonPropertyName("node")] string Node,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record RegistrationStatusResponse(
        [property: JsonPropertyName("node")] string Node,
        [property: JsonPropertyName("registered")] bool Registered);
}
