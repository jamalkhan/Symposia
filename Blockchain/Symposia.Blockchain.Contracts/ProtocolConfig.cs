using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Contracts;

// ProtocolConfig bindings (#167): the one copy every Symposia .NET component uses; ContractAbiTests pins them to the forge ABI.

[Function("config", "address")]
public sealed class ConfigFunction : FunctionMessage;

[Function("getUint", "uint256")]
public sealed class GetUintFunction : FunctionMessage { [Parameter("bytes32", "key", 1)] public byte[] Key { get; set; } = []; }

[Function("getAddress", "address")]
public sealed class GetAddressFunction : FunctionMessage { [Parameter("bytes32", "key", 1)] public byte[] Key { get; set; } = []; }

/// <summary>Config keys the .NET side resolves contract addresses with (#227).</summary>
public static class ConfigKeys
{
    public static readonly byte[] VerifierAssignments = Nethereum.Util.Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("reward.verifier.assignments"));

    public static readonly byte[] MetricSigner = Nethereum.Util.Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("reward.metric.signer"));
}
