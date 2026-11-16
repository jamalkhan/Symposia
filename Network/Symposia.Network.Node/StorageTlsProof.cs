using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node;

/// <summary>
/// Binds a storage node's self-signed TLS certificate to its on-chain identity (issue #142): the node
/// signs (EIP-191, its node key) a digest of the network, its address, the certificate's SHA-256
/// fingerprint and its expiry. A gateway that recovers the node's registry address from the proof pins
/// that fingerprint until that expiry, so no certificate authority is involved and an old, replaced
/// certificate can't be replayed once it has expired.
/// The fingerprint and pin matching are <see cref="Symposia.Security.TlsPinning.TlsPin"/> (#252), shared with the
/// Credential Authority; this class is only the on-chain proof.
/// </summary>
public static class StorageTlsProof
{
    public const string Domain = "Symposia.StorageTls.v2";

    private static readonly EthereumMessageSigner MessageSigner = new();

    public static byte[] Digest(ulong chainId, string registryAddress, string node, byte[] fingerprint, long notAfterUnixSeconds)
    {
        if (fingerprint.Length != 32)
        {
            throw new ArgumentException("A TLS fingerprint is the 32-byte SHA-256 of the certificate.", nameof(fingerprint));
        }

        return Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncodedPacked(
            new ABIValue("string", Domain),
            new ABIValue("uint256", chainId),
            new ABIValue("address", registryAddress),
            new ABIValue("address", node),
            new ABIValue("bytes32", fingerprint),
            new ABIValue("uint64", (ulong)notAfterUnixSeconds)));
    }

    public static string Sign(EthECKey key, ulong chainId, string registryAddress, byte[] fingerprint, long notAfterUnixSeconds) =>
        MessageSigner.Sign(Digest(chainId, registryAddress, key.GetPublicAddress(), fingerprint, notAfterUnixSeconds), key);

    /// <summary>
    /// Null when <paramref name="proof"/> is <paramref name="node"/>'s signature over this fingerprint and
    /// expiry on this network and the expiry is still ahead; otherwise the reason it isn't.
    /// </summary>
    public static string? Validate(
        ulong chainId, string registryAddress, string node, string? fingerprintHex, long? notAfterUnixSeconds, string? proof, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(proof))
        {
            return "no certificate proof";
        }

        // A 65-byte r||s||v signature in hex; checked up front so malformed input never reaches the decoder.
        var sig = proof.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? proof[2..] : proof;
        if (sig.Length != 130 || !sig.All(Uri.IsHexDigit))
        {
            return "malformed certificate proof";
        }

        if (fingerprintHex is not { Length: 64 } || !fingerprintHex.All(Uri.IsHexDigit))
        {
            return "malformed certificate fingerprint";
        }

        if (notAfterUnixSeconds is not { } notAfter || notAfter <= now.ToUnixTimeSeconds())
        {
            return "certificate proof is missing its expiry or has expired";
        }

        string? signer;
        try
        {
            signer = MessageSigner.EcRecover(Digest(chainId, registryAddress, node, Convert.FromHexString(fingerprintHex), notAfter), proof);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return "malformed certificate proof";
        }

        return string.Equals(signer, node, StringComparison.OrdinalIgnoreCase)
            ? null
            : "certificate proof is not signed by this node for this network";
    }
}
