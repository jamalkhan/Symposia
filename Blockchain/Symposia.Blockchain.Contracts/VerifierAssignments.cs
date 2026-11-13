using System.Numerics;
using Nethereum.ABI;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Util;

namespace Symposia.Blockchain.Contracts;

// VerifierAssignments bindings (#227): the one copy every Symposia .NET component uses; ContractAbiTests pins them to the forge ABI.

[Function("syncMany")]
public sealed class SyncManyFunction : FunctionMessage { [Parameter("address[]", "nodes", 1)] public List<string> Nodes { get; set; } = []; }

[Function("seedEpoch")]
public sealed class SeedEpochFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

[Function("seedOf", "bytes32")]
public sealed class SeedOfFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

[Function("isFinalized", "bool")]
public sealed class IsVerifierEpochFinalizedFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

[Function("finalize", "bool")]
public sealed class FinalizeVerifierEpochFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
    [Parameter("uint256", "maxTargets", 2)] public BigInteger MaxTargets { get; set; }
}

[Function("assignmentsOf", typeof(AssignmentsPageOutput))]
public sealed class AssignmentsOfFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
    [Parameter("address", "verifier", 2)] public string Verifier { get; set; } = "";
    [Parameter("uint256", "offset", 3)] public BigInteger Offset { get; set; }
    [Parameter("uint256", "limit", 4)] public BigInteger Limit { get; set; }
}

[FunctionOutput]
public sealed class AssignmentsPageOutput : IFunctionOutputDTO
{
    [Parameter("address[]", "targets", 1)] public List<string> Targets { get; set; } = [];
    [Parameter("uint8[]", "slots", 2)] public List<byte> Slots { get; set; } = [];
    [Parameter("uint256", "next", 3)] public BigInteger Next { get; set; }
}

[Function("targetCount", "uint256")]
public sealed class VerifierTargetCountFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

[Function("countsOf", typeof(VerifierCountsOutput))]
public sealed class VerifierCountsOfFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
    [Parameter("address", "verifier", 2)] public string Verifier { get; set; } = "";
}

[FunctionOutput]
public sealed class VerifierCountsOutput : IFunctionOutputDTO
{
    [Parameter("uint32", "assigned", 1)] public uint Assigned { get; set; }
    [Parameter("uint32", "submitted", 2)] public uint Submitted { get; set; }
    [Parameter("uint32", "judged", 3)] public uint Judged { get; set; }
    [Parameter("uint32", "upheld", 4)] public uint Upheld { get; set; }
}

public sealed class AttestationItem
{
    [Parameter("address", "target", 1)] public string Target { get; set; } = "";
    [Parameter("uint8", "slot", 2)] public byte Slot { get; set; }
    [Parameter("uint256[]", "values", 3)] public List<BigInteger> Values { get; set; } = [];
}

[Function("attest")]
public sealed class AttestFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
    [Parameter("address", "verifier", 2)] public string Verifier { get; set; } = "";
    [Parameter("tuple[]", "items", 3)] public List<AttestationItem> Items { get; set; } = [];
    [Parameter("bytes", "signature", 4)] public byte[] Signature { get; set; } = [];
}

/// <summary>
/// <c>VerifierAssignments.attestationDigest</c> off-chain:
/// <c>keccak256(abi.encode(contract, chainid, epoch, verifier, keccak256(abi.encode(items))))</c>, signed with the
/// eth-signed-message prefix by the Verifier's node key (#227).
/// </summary>
public static class AttestationDigest
{
    public static byte[] Hash(string contract, ulong chainId, ulong epoch, string verifier, IReadOnlyList<AttestationItem> items)
    {
        // abi.encode(Item[]) of a single dynamic-tuple-array argument.
        var encodedItems = new Nethereum.ABI.FunctionEncoding.ParametersEncoder().EncodeParameters(
            [new Nethereum.ABI.Model.Parameter("tuple[]", "items", 1) { DecodedType = typeof(AttestationItem) }], [items.ToList()]);
        return Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncoded(
            new ABIValue("address", contract), new ABIValue("uint256", new BigInteger(chainId)), new ABIValue("uint256", new BigInteger(epoch)),
            new ABIValue("address", verifier), new ABIValue("bytes32", Sha3Keccack.Current.CalculateHash(encodedItems))));
    }
}
