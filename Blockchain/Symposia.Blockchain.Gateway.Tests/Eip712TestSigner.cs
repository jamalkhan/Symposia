using System.Text;
using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// Reconstructs the EIP-712 digests the NodeRegistry/EpochRootRegistry
/// contracts verify, so tests can sign payloads the same way a real Blob
/// Storage node (per issue #109) would, without depending on Nethereum's
/// higher-level typed-data helpers matching the contracts' exact encoding.
/// </summary>
public static class Eip712TestSigner
{
    private static readonly Sha3Keccack Keccak = Sha3Keccack.Current;

    private static byte[] HashUtf8(string value) => Keccak.CalculateHash(Encoding.UTF8.GetBytes(value));

    private static byte[] DomainSeparator(string name, string verifyingContract, ulong chainId)
    {
        var domainTypeHash =
            HashUtf8("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");
        var abiEncode = new ABIEncode();
        return Keccak.CalculateHash(abiEncode.GetABIEncoded(
            new ABIValue("bytes32", domainTypeHash),
            new ABIValue("bytes32", HashUtf8(name)),
            new ABIValue("bytes32", HashUtf8("1")),
            new ABIValue("uint256", chainId),
            new ABIValue("address", verifyingContract)));
    }

    private static byte[] TypedDataDigest(byte[] domainSeparator, byte[] structHash)
    {
        var prefix = new byte[] { 0x19, 0x01 };
        var payload = prefix.Concat(domainSeparator).Concat(structHash).ToArray();
        return Keccak.CalculateHash(payload);
    }

    public static byte[] SignRegister(string privateKeyHex, string nodeAddress, string contractAddress, ulong chainId)
    {
        var abiEncode = new ABIEncode();
        var typeHash = HashUtf8("Register(address node)");
        var structHash = Keccak.CalculateHash(abiEncode.GetABIEncoded(
            new ABIValue("bytes32", typeHash),
            new ABIValue("address", nodeAddress)));

        var digest = TypedDataDigest(DomainSeparator("Symposia.NodeRegistry", contractAddress, chainId), structHash);
        return Sign(privateKeyHex, digest);
    }

    public static byte[] SignSubmitRoot(
        string privateKeyHex, string nodeAddress, ulong epoch, byte[] root, string contractAddress, ulong chainId)
    {
        var abiEncode = new ABIEncode();
        var typeHash = HashUtf8("SubmitRoot(address node,uint64 epoch,bytes32 root)");
        var structHash = Keccak.CalculateHash(abiEncode.GetABIEncoded(
            new ABIValue("bytes32", typeHash),
            new ABIValue("address", nodeAddress),
            new ABIValue("uint64", epoch),
            new ABIValue("bytes32", root)));

        var digest = TypedDataDigest(DomainSeparator("Symposia.EpochRootRegistry", contractAddress, chainId), structHash);
        return Sign(privateKeyHex, digest);
    }

    private static byte[] Sign(string privateKeyHex, byte[] digest)
    {
        var key = new EthECKey(privateKeyHex);
        var signature = key.SignAndCalculateV(digest);
        // Contracts expect the packed r || s || v (v as a single byte, 27/28) layout,
        // matching Solidity's abi.encodePacked(r, s, v) used on the forge-test side.
        return signature.R.Concat(signature.S).Concat([signature.V[0]]).ToArray();
    }
}
