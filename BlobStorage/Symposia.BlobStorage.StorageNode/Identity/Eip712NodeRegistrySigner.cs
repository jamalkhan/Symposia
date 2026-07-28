using System.Text;
using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.BlobStorage.StorageNode.Identity;

/// <summary>
/// Builds and signs the EIP-712 "Register(address node)" digest that the
/// on-chain <c>NodeRegistry</c> contract (issue #110) verifies, so this
/// node's own private key can prove control of its address without that key
/// ever leaving this process (issue #109, Functional Requirement 3/7).
/// Domain and typehash mirror <c>NodeRegistry.sol</c> exactly (EIP712("Symposia.NodeRegistry", "1")).
/// </summary>
public static class Eip712NodeRegistrySigner
{
    private static readonly Sha3Keccack Keccak = Sha3Keccack.Current;

    public static byte[] SignRegister(EthECKey key, string nodeRegistryAddress, ulong chainId)
    {
        var abiEncode = new ABIEncode();
        var typeHash = HashUtf8("Register(address node)");
        var structHash = Keccak.CalculateHash(abiEncode.GetABIEncoded(
            new ABIValue("bytes32", typeHash),
            new ABIValue("address", key.GetPublicAddress())));

        var digest = TypedDataDigest(DomainSeparator(nodeRegistryAddress, chainId), structHash);
        return Sign(key, digest);
    }

    private static byte[] HashUtf8(string value) => Keccak.CalculateHash(Encoding.UTF8.GetBytes(value));

    private static byte[] DomainSeparator(string verifyingContract, ulong chainId)
    {
        var domainTypeHash =
            HashUtf8("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");
        var abiEncode = new ABIEncode();
        return Keccak.CalculateHash(abiEncode.GetABIEncoded(
            new ABIValue("bytes32", domainTypeHash),
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

    /// <summary>
    /// Signs an arbitrary digest with the node's key, in the same
    /// packed r||s||v layout the chain contracts expect. Used both for
    /// registration and for smoke-testing that the registered identity
    /// remains a valid signer for later on-chain messages (AC7).
    /// </summary>
    public static byte[] Sign(EthECKey key, byte[] digest)
    {
        var signature = key.SignAndCalculateV(digest);
        return signature.R.Concat(signature.S).Concat([signature.V[0]]).ToArray();
    }
}
