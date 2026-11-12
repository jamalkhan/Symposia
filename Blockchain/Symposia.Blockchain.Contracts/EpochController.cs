using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Contracts;

// EpochController bindings (#167): the one copy every Symposia .NET component uses; ContractAbiTests pins them to the forge ABI.

[Function("epochStart", "uint256")]
public sealed class EpochStartFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

[Function("epochLength", "uint256")]
public sealed class EpochLengthFunction : FunctionMessage;

[Function("currentEpoch", "uint256")]
public sealed class CurrentEpochFunction : FunctionMessage;

[Function("acceptsReports", "bool")]
public sealed class AcceptsReportsFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }
