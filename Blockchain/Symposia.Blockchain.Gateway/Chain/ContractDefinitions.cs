using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Gateway.Chain;

[Function("register")]
public sealed class RegisterFunction : FunctionMessage
{
    [Parameter("address", "node", 1)]
    public string Node { get; set; } = "";

    [Parameter("string", "endpoint", 2)]
    public string Endpoint { get; set; } = "";

    [Parameter("uint256", "nonce", 3)]
    public System.Numerics.BigInteger RegistrationNonce { get; set; }

    [Parameter("bytes", "signature", 4)]
    public byte[] Signature { get; set; } = [];
}

[Function("endpointOf", "string")]
public sealed class EndpointOfFunction : FunctionMessage
{
    [Parameter("address", "node", 1)]
    public string Node { get; set; } = "";
}

[Function("nonceOf", "uint256")]
public sealed class NonceOfFunction : FunctionMessage
{
    [Parameter("address", "node", 1)]
    public string Node { get; set; } = "";
}

[Function("nodeCount", "uint256")]
public sealed class NodeCountFunction : FunctionMessage;

[Function("nodeAt", "address")]
public sealed class NodeAtFunction : FunctionMessage
{
    [Parameter("uint256", "index", 1)]
    public System.Numerics.BigInteger Index { get; set; }
}

[Function("isRegistered", "bool")]
public sealed class IsRegisteredFunction : FunctionMessage
{
    [Parameter("address", "node", 1)]
    public string Node { get; set; } = "";
}

[Function("submitRoot")]
public sealed class SubmitRootFunction : FunctionMessage
{
    [Parameter("address", "node", 1)]
    public string Node { get; set; } = "";

    [Parameter("uint64", "epoch", 2)]
    public ulong Epoch { get; set; }

    [Parameter("bytes32", "root", 3)]
    public byte[] Root { get; set; } = [];

    [Parameter("bytes", "signature", 4)]
    public byte[] Signature { get; set; } = [];
}

[Function("getLatestRoot", typeof(GetLatestRootOutputDTO))]
public sealed class GetLatestRootFunction : FunctionMessage
{
    [Parameter("address", "node", 1)]
    public string Node { get; set; } = "";
}

[FunctionOutput]
public sealed class GetLatestRootOutputDTO : IFunctionOutputDTO
{
    [Parameter("uint64", "epoch", 1)]
    public ulong Epoch { get; set; }

    [Parameter("bytes32", "root", 2)]
    public byte[] Root { get; set; } = [];
}

[Function("getRoot", "bytes32")]
public sealed class GetRootFunction : FunctionMessage
{
    [Parameter("address", "node", 1)]
    public string Node { get; set; } = "";

    [Parameter("uint64", "epoch", 2)]
    public ulong Epoch { get; set; }
}

// ProtocolActivations (issue #145).
[Function("featureCount", "uint256")]
public sealed class FeatureCountFunction : FunctionMessage;

[Function("featureAt", typeof(FeatureAtOutputDTO))]
public sealed class FeatureAtFunction : FunctionMessage
{
    [Parameter("uint256", "index", 1)]
    public System.Numerics.BigInteger Index { get; set; }
}

[FunctionOutput]
public sealed class FeatureAtOutputDTO : IFunctionOutputDTO
{
    [Parameter("string", "name", 1)]
    public string Name { get; set; } = "";

    [Parameter("uint64", "activatesAt", 2)]
    public ulong ActivatesAt { get; set; }
}

[Function("minProtocolVersion", typeof(MinProtocolVersionOutputDTO))]
public sealed class MinProtocolVersionFunction : FunctionMessage;

[FunctionOutput]
public sealed class MinProtocolVersionOutputDTO : IFunctionOutputDTO
{
    [Parameter("uint32", "effective", 1)]
    public uint Effective { get; set; }

    [Parameter("uint32", "pending", 2)]
    public uint Pending { get; set; }

    [Parameter("uint64", "pendingAt", 3)]
    public ulong PendingAt { get; set; }
}
