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
    public async Task<PeerEntry> VerifyAsync(string endpoint, string? expectedAddress, CancellationToken ct)
    {
        var o = options.Value;
        var nonce = RandomNumberGenerator.GetBytes(PeerHandshake.NonceLength);

        HandshakeResponse response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(o.HandshakeTimeoutSeconds));
            response = await handshakeClient.ChallengeAsync(
                endpoint, new HandshakeRequest(identity.NodeId, "0x" + Convert.ToHexString(nonce)), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(PeerFailureReason.Timeout, expectedAddress);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or UriFormatException)
        {
            return Fail(PeerFailureReason.Unreachable, expectedAddress);
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

        var status = await registry.GetStatusAsync(recovered, ct);
        if (!status.Registered)
        {
            return Fail(PeerFailureReason.NotRegistered, recovered);
        }

        return new PeerEntry(endpoint, recovered, PeerStatus.Recognized, PeerFailureReason.None, time.GetUtcNow());

        PeerEntry Fail(PeerFailureReason reason, string? address) =>
            new(endpoint, address, PeerStatus.Unrecognized, reason, time.GetUtcNow());
    }

    internal static bool SameAddress(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
