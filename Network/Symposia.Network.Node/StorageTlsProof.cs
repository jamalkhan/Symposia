using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node;

/// <summary>
/// Binds a storage node's self-signed TLS certificate to its on-chain identity (issue #142): the node
/// signs (EIP-191, its node key) a digest of the network, its address and the certificate's SHA-256
/// fingerprint. A gateway that recovers the node's registry address from the proof can pin that
/// fingerprint, so no certificate authority is involved.
/// </summary>
public static class StorageTlsProof
{
    public const string Domain = "Symposia.StorageTls.v1";

    private static readonly EthereumMessageSigner MessageSigner = new();

    public static byte[] Digest(ulong chainId, string registryAddress, string node, byte[] fingerprint)
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
            new ABIValue("bytes32", fingerprint)));
    }

    public static string Sign(EthECKey key, ulong chainId, string registryAddress, byte[] fingerprint) =>
        MessageSigner.Sign(Digest(chainId, registryAddress, key.GetPublicAddress(), fingerprint), key);

    /// <summary>True when <paramref name="proof"/> is <paramref name="node"/>'s signature over this fingerprint on this network.</summary>
    public static bool Verify(ulong chainId, string registryAddress, string node, string fingerprintHex, string? proof)
    {
        if (string.IsNullOrEmpty(proof) || fingerprintHex.Length != 64)
        {
            return false;
        }

        try
        {
            var signer = MessageSigner.EcRecover(Digest(chainId, registryAddress, node, Convert.FromHexString(fingerprintHex)), proof);
            return string.Equals(signer, node, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return false;
        }
    }
}
