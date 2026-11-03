using System.Text;
using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node;

/// <summary>
/// Builds and signs the EIP-712 "Register(address node,string endpoint,uint256 nonce)"
/// digest that the on-chain <c>NodeRegistry</c> contract verifies (issues #110, #129),
/// so this node's own private key proves control of its address and endpoint without
/// that key ever leaving this process. Domain and typehash mirror <c>NodeRegistry.sol</c>
/// exactly (EIP712("Symposia.NodeRegistry", "1")).
/// </summary>
public static class Eip712NodeRegistrySigner
{
    private static readonly Sha3Keccack Keccak = Sha3Keccack.Current;

    public static byte[] SignRegister(EthECKey key, string endpoint, ulong nonce, string nodeRegistryAddress, ulong chainId)
    {
        var abiEncode = new ABIEncode();
        var structHash = Keccak.CalculateHash(abiEncode.GetABIEncoded(
            new ABIValue("bytes32", HashUtf8("Register(address node,string endpoint,uint256 nonce)")),
            new ABIValue("address", key.GetPublicAddress()),
            new ABIValue("bytes32", HashUtf8(endpoint)),
            new ABIValue("uint256", nonce)));

        var digest = TypedDataDigest(DomainSeparator(nodeRegistryAddress, chainId), structHash);
        return Sign(key, digest);
    }

    private static byte[] HashUtf8(string value) => Keccak.CalculateHash(Encoding.UTF8.GetBytes(value));

    private static byte[] DomainSeparator(string verifyingContract, ulong chainId)
    {
        var abiEncode = new ABIEncode();
        return Keccak.CalculateHash(abiEncode.GetABIEncoded(
            new ABIValue("bytes32", HashUtf8("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)")),
            new ABIValue("bytes32", HashUtf8("Symposia.NodeRegistry")),
            new ABIValue("bytes32", HashUtf8("1")),
            new ABIValue("uint256", chainId),
            new ABIValue("address", verifyingContract)));
    }

    private static byte[] TypedDataDigest(byte[] domainSeparator, byte[] structHash)
    {
        var prefix = new byte[] { 0x19, 0x01 };
        return Keccak.CalculateHash(prefix.Concat(domainSeparator).Concat(structHash).ToArray());
    }

    /// <summary>Signs a digest in the packed 65-byte r||s||v layout the chain contracts expect.</summary>
    public static byte[] Sign(EthECKey key, byte[] digest)
    {
        var signature = key.SignAndCalculateV(digest);
        // R and S are big-endian integers that drop leading zero bytes, so
        // they must be left-padded to 32 bytes or ~1 in 128 signatures is
        // malformed.
        return PadTo32(signature.R).Concat(PadTo32(signature.S)).Concat([signature.V[0]]).ToArray();
    }

    private static byte[] PadTo32(byte[] value) =>
        value.Length >= 32 ? value[^32..] : new byte[32 - value.Length].Concat(value).ToArray();
}
