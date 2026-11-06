using Symposia.Blockchain.Contracts;
using Nethereum.ABI;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node.Telemetry;

/// <summary>Maps node-side telemetry records onto the shared contract structs (#167).</summary>
public static class TelemetryAbi
{
    public static MetricValuesInput ToInput(MetricValues v) => new()
    {
        UptimeBps = v.UptimeBps,
        ReadThroughputKBps = v.ReadThroughputKBps,
        WriteThroughputKBps = v.WriteThroughputKBps,
        BandwidthInKbps = v.BandwidthInKbps,
        BandwidthOutKbps = v.BandwidthOutKbps,
        LatencyMs = v.LatencyMs,
        RetrievalMs = v.RetrievalMs,
    };

    public static AggregatedMetricInput ToInput(AggregatedMetricRecord r) => new()
    {
        Node = r.NodeId,
        SelfReported = ToInput(r.SelfReported),
        VerifierAttested = ToInput(r.VerifierAttested),
        HeartbeatsReceived = r.HeartbeatsReceived,
        HeartbeatsExpected = r.HeartbeatsExpected,
        AttestationCount = r.AttestationCount,
        FirstCheckpoint = r.FirstCheckpoint,
        LastCheckpoint = r.LastCheckpoint,
        AvailableBytes = r.AvailableBytes,
        UsedBytes = r.UsedBytes,
    };
}

/// <summary>
/// EIP-712 digests for <c>TelemetryCheckpointRegistry</c> (domain "SymposiaTelemetry" v1), so
/// committee members and nodes sign exactly what the contract verifies. Signatures are 65-byte
/// r‖s‖v over the digest, with no EIP-191 prefix.
/// </summary>
public sealed class TelemetryEip712(ulong chainId, string registry)
{
    public static readonly byte[] CheckpointTypeHash = Keccak(
        "TelemetryCheckpoint(uint256 epochIndex,uint256 checkpointIndex,bytes32 merkleRoot,bytes32 blobAddress,uint32 sampleCount)");
    public static readonly byte[] AggregateTypeHash = Keccak(
        "AggregatedRecords(uint256 epochIndex,uint256 chunkIndex,uint256 chunkCount,bytes32 recordsHash)");
    public static readonly byte[] SelfCheckpointTypeHash = Keccak(
        "SelfCheckpoint(address node,uint256 epochIndex,uint256 checkpointIndex,bytes32 merkleRoot,bytes32 blobAddress,uint32 sampleCount)");

    private readonly byte[] _domainSeparator = Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncoded(
        new ABIValue("bytes32", Keccak("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)")),
        new ABIValue("bytes32", Keccak("SymposiaTelemetry")),
        new ABIValue("bytes32", Keccak("1")),
        new ABIValue("uint256", chainId),
        new ABIValue("address", registry)));

    public byte[] CheckpointDigest(ulong epoch, ushort index, byte[] merkleRoot, byte[] blobAddress, uint sampleCount) =>
        Typed(new ABIEncode().GetABIEncoded(
            new ABIValue("bytes32", CheckpointTypeHash), new ABIValue("uint256", epoch), new ABIValue("uint256", index),
            new ABIValue("bytes32", merkleRoot), new ABIValue("bytes32", blobAddress), new ABIValue("uint32", sampleCount)));

    public byte[] SelfCheckpointDigest(string node, ulong epoch, ushort index, byte[] merkleRoot, byte[] blobAddress, uint sampleCount) =>
        Typed(new ABIEncode().GetABIEncoded(
            new ABIValue("bytes32", SelfCheckpointTypeHash), new ABIValue("address", node), new ABIValue("uint256", epoch),
            new ABIValue("uint256", index), new ABIValue("bytes32", merkleRoot), new ABIValue("bytes32", blobAddress),
            new ABIValue("uint32", sampleCount)));

    public byte[] RecordsDigest(ulong epoch, int chunkIndex, int chunkCount, IReadOnlyList<AggregatedMetricInput> records) =>
        Typed(new ABIEncode().GetABIEncoded(
            new ABIValue("bytes32", AggregateTypeHash), new ABIValue("uint256", epoch), new ABIValue("uint256", chunkIndex),
            new ABIValue("uint256", chunkCount), new ABIValue("bytes32", RecordsHash(records))));

    /// <summary><c>keccak256(abi.encode(inputs))</c>, matching <c>hashRecords</c>.</summary>
    public static byte[] RecordsHash(IReadOnlyList<AggregatedMetricInput> records) =>
        Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIParamsEncoded(new RecordsParam { Inputs = [.. records] }));

    private sealed class RecordsParam
    {
        [Parameter("tuple[]", "inputs", 1, structTypeName: "AggregatedMetricInput[]")]
        public List<AggregatedMetricInput> Inputs { get; set; } = [];
    }

    public static string Sign(EthECKey key, byte[] digest)
    {
        var sig = key.SignAndCalculateV(digest);
        return (sig.R.PadLeft32().Concat(sig.S.PadLeft32()).Concat(sig.V)).ToArray().ToHex(true);
    }

    /// <summary>The signer of <paramref name="signature"/> over <paramref name="digest"/>, or null if malformed.</summary>
    public static string? Recover(byte[] digest, string signature)
    {
        try
        {
            var bytes = signature.HexToByteArray();
            if (bytes.Length != 65)
            {
                return null;
            }

            var ecdsa = EthECDSASignatureFactory.FromComponents(bytes[..32], bytes[32..64], bytes[64]);
            return EthECKey.RecoverFromSignature(ecdsa, digest).GetPublicAddress();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private byte[] Typed(byte[] structEncoded) =>
        Sha3Keccack.Current.CalculateHash([0x19, 0x01, .. _domainSeparator, .. Sha3Keccack.Current.CalculateHash(structEncoded)]);

    private static byte[] Keccak(string s) => Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes(s));
}

internal static class ByteExtensions
{
    public static byte[] PadLeft32(this byte[] b) => b.Length >= 32 ? b[^32..] : [.. new byte[32 - b.Length], .. b];
}
