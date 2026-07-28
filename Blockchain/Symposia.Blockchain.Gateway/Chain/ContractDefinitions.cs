using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Gateway.Chain;

[Function("register")]
public sealed class RegisterFunction : FunctionMessage
{
    [Parameter("address", "node", 1)]
    public string Node { get; set; } = "";

    [Parameter("bytes", "signature", 2)]
    public byte[] Signature { get; set; } = [];
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
