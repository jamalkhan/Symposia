using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Contracts;

// SYMToken bindings for LocalDev self-staking (#200): the dev minter funds nodes, nodes approve the staking registry.

[Function("mint")]
public sealed class MintFunction : FunctionMessage
{
    [Parameter("address", "to", 1)] public string To { get; set; } = "";
    [Parameter("uint256", "amount", 2)] public BigInteger Amount { get; set; }
}

[Function("approve", "bool")]
public sealed class ApproveFunction : FunctionMessage
{
    [Parameter("address", "spender", 1)] public string Spender { get; set; } = "";
    [Parameter("uint256", "value", 2)] public BigInteger Value { get; set; }
}

[Function("balanceOf", "uint256")]
public sealed class BalanceOfFunction : FunctionMessage { [Parameter("address", "account", 1)] public string Account { get; set; } = ""; }

/// <summary><c>ConfigKeys</c> the staking flow reads.</summary>
public static class ConfigKey
{
    public static readonly byte[] TokenAddress = Nethereum.Util.Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("token.address"));
}
