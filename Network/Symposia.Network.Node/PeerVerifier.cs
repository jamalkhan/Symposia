using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Symposia.Network.Node;

/// <summary>
/// Dials one peer endpoint and decides whether it is a recognized node
/// (issue #129 FR-D2): the response signature must recover to the
/// responder's claimed address, that address must match the registry's
/// entry for the endpoint (when known), and it must be registered on-chain.
/// </summary>
public sealed class PeerVerifier(
    NodeIdentity identity,
    IPeerHandshakeClient handshakeClient,
    INodeRegistryClient registry,
    IOptions<NodeNetworkOptions> options,
    TimeProvider time)
{
    /// <param name="expectedAddress">
    /// The registry's address for this endpoint. Registry-sourced peers are
    /// known registered from the listing itself, so only seed peers (null)
    /// need a separate registration lookup.
    /// </param>
    public async Task<PeerEntry> VerifyAsync(string endpoint, string? expectedAddress, CancellationToken ct)
    {
        var o = options.Value;
        if (!NodeEndpoint.IsDialable(endpoint))
        {
            return Fail(PeerFailureReason.EndpointNotDialable, expectedAddress);
        }

        var nonce = RandomNumberGenerator.GetBytes(PeerHandshake.NonceLength);

        HandshakeResponse? response;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(o.HandshakeTimeoutSeconds), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            response = await handshakeClient.ChallengeAsync(
                endpoint, new HandshakeRequest(identity.NodeId, "0x" + Convert.ToHexString(nonce)), linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(PeerFailureReason.Timeout, expectedAddress);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or UriFormatException)
        {
            return Fail(PeerFailureReason.Unreachable, expectedAddress);
        }

        if (response is null || string.IsNullOrEmpty(response.Address) || string.IsNullOrEmpty(response.Signature))
        {
            return Fail(PeerFailureReason.SignatureInvalid, expectedAddress);
        }

        var recovered = PeerHandshake.Recover(o.ChainId, o.NodeRegistryAddress, identity.NodeId, response.Address, nonce, response.Signature);
        if (recovered is null || !SameAddress(recovered, response.Address))
        {
            return Fail(PeerFailureReason.SignatureInvalid, response.Address);
        }

        if (expectedAddress is not null && !SameAddress(expectedAddress, recovered))
        {
            return Fail(PeerFailureReason.AddressMismatch, recovered);
        }

        if (expectedAddress is null && !(await registry.GetStatusAsync(recovered, ct)).Registered)
        {
            return Fail(PeerFailureReason.NotRegistered, recovered);
        }

        return new PeerEntry(endpoint, expectedAddress, recovered, PeerStatus.Recognized, PeerFailureReason.None, time.GetUtcNow());

        PeerEntry Fail(PeerFailureReason reason, string? address) =>
            new(endpoint, expectedAddress, address, PeerStatus.Unrecognized, reason, time.GetUtcNow());
    }

    internal static bool SameAddress(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
