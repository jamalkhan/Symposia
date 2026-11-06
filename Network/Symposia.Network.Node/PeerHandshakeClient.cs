using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Symposia.Network.Node;

public sealed record HandshakeRequest(
    [property: JsonPropertyName("dialer")] string Dialer,
    [property: JsonPropertyName("nonce")] string Nonce);

public sealed record HandshakeResponse(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>Sends a handshake challenge to a peer endpoint, abstracted for discovery tests.</summary>
public interface IPeerHandshakeClient
{
    /// <summary>Returns the peer's response, or throws on transport failure/timeout.</summary>
    Task<HandshakeResponse> ChallengeAsync(string endpoint, HandshakeRequest request, CancellationToken ct = default);
}

public sealed class PeerHandshakeClient(HttpClient http) : IPeerHandshakeClient
{
    public async Task<HandshakeResponse> ChallengeAsync(string endpoint, HandshakeRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(new Uri(new Uri(endpoint), "/v1/peer/handshake"), request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HandshakeResponse>(ct)
            ?? throw new HttpRequestException("Empty handshake response.");
    }
}
