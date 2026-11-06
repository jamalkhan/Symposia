using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Contracts;

// ProtocolConfig bindings (#167): the one copy every Symposia .NET component uses; ContractAbiTests pins them to the forge ABI.

[Function("config", "address")]
public sealed class ConfigFunction : FunctionMessage;

[Function("getUint", "uint256")]
public sealed class GetUintFunction : FunctionMessage { [Parameter("bytes32", "key", 1)] public byte[] Key { get; set; } = []; }
