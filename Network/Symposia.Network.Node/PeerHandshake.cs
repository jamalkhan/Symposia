using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node;

/// <summary>
/// The challenge/response message both sides of a peer handshake compute
/// (issue #129 FR-D1). The digest binds the protocol version, chain,
/// registry, dialer, responder, and a dialer-chosen nonce, so a signature
/// cannot be replayed into another session or relayed to another dialer.
/// Signed with the EIP-191 personal-message prefix so it can never be
/// mistaken for a transaction or an EIP-712 registry payload.
/// </summary>
public static class PeerHandshake
{
    public const string Domain = "Symposia.PeerHandshake.v1";
    public const int NonceLength = 32;

    private static readonly EthereumMessageSigner MessageSigner = new();

    public static byte[] Digest(ulong chainId, string registryAddress, string dialer, string responder, byte[] nonce)
    {
        if (nonce.Length != NonceLength)
        {
            throw new ArgumentException($"Handshake nonce must be {NonceLength} bytes.", nameof(nonce));
        }

        return Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncodedPacked(
            new ABIValue("string", Domain),
            new ABIValue("uint256", chainId),
            new ABIValue("address", registryAddress),
            new ABIValue("address", dialer),
            new ABIValue("address", responder),
            new ABIValue("bytes32", nonce)));
    }

    public static string Sign(EthECKey key, ulong chainId, string registryAddress, string dialer, byte[] nonce) =>
        MessageSigner.Sign(Digest(chainId, registryAddress, dialer, key.GetPublicAddress(), nonce), key);

    /// <summary>Recovers the signer address, or null if the signature is malformed.</summary>
    public static string? Recover(
        ulong chainId, string registryAddress, string dialer, string responder, byte[] nonce, string signature)
    {
        try
        {
            return MessageSigner.EcRecover(Digest(chainId, registryAddress, dialer, responder, nonce), signature);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }
}
