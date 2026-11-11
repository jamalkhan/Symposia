using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Contracts;

// StakingNodeRegistry bindings (#167): the one copy every Symposia .NET component uses; ContractAbiTests pins them to the forge ABI.

[Event("ServiceRegistered")]
public sealed class ServiceRegisteredEvent : IEventDTO
{
    [Parameter("address", "node", 1, true)] public string Node { get; set; } = "";
    [Parameter("uint8", "service", 2, true)] public byte Service { get; set; }
    [Parameter("uint256", "capacity", 3, false)] public BigInteger Capacity { get; set; }
    [Parameter("bytes32", "region", 4, false)] public byte[] Region { get; set; } = [];
    [Parameter("uint256", "stake", 5, false)] public BigInteger Stake { get; set; }
}

[Event("ServiceStakeChanged")]
public sealed class ServiceStakeChangedEvent : IEventDTO
{
    [Parameter("address", "node", 1, true)] public string Node { get; set; } = "";
    [Parameter("uint8", "service", 2, true)] public byte Service { get; set; }
    [Parameter("uint256", "previousStake", 3, false)] public BigInteger PreviousStake { get; set; }
    [Parameter("uint256", "newStake", 4, false)] public BigInteger NewStake { get; set; }
    [Parameter("uint8", "reason", 5, false)] public byte Reason { get; set; }
}

[Event("ServiceUnstakeRequested")]
public sealed class ServiceUnstakeRequestedEvent : IEventDTO
{
    [Parameter("address", "node", 1, true)] public string Node { get; set; } = "";
    [Parameter("uint8", "service", 2, true)] public byte Service { get; set; }
    [Parameter("uint256", "amount", 3, false)] public BigInteger Amount { get; set; }
    [Parameter("uint256", "cooldownEnd", 4, false)] public BigInteger CooldownEnd { get; set; }
}

[Event("ServiceStatusChanged")]
public sealed class ServiceStatusChangedEvent : IEventDTO
{
    [Parameter("address", "node", 1, true)] public string Node { get; set; } = "";
    [Parameter("uint8", "service", 2, true)] public byte Service { get; set; }
    [Parameter("uint8", "previousStatus", 3, false)] public byte PreviousStatus { get; set; }
    [Parameter("uint8", "newStatus", 4, false)] public byte NewStatus { get; set; }
}

// Staking calls for LocalDev self-staking (#200).

[Function("registerService")]
public sealed class RegisterServiceFunction : FunctionMessage
{
    [Parameter("uint8", "service", 1)] public byte Service { get; set; }
    [Parameter("uint256", "capacity", 2)] public BigInteger Capacity { get; set; }
    [Parameter("bytes32", "region", 3)] public byte[] Region { get; set; } = new byte[32];
    [Parameter("uint256", "stakeAmount", 4)] public BigInteger StakeAmount { get; set; }
}

[Function("serviceStatusOf", "uint8")]
public sealed class ServiceStatusOfFunction : FunctionMessage
{
    [Parameter("address", "node", 1)] public string Node { get; set; } = "";
    [Parameter("uint8", "service", 2)] public byte Service { get; set; }
}

[Function("minStakeFor", "uint256")]
public sealed class MinStakeForFunction : FunctionMessage
{
    [Parameter("uint8", "nodeType", 1)] public byte NodeType { get; set; }
    [Parameter("uint256", "capacity", 2)] public BigInteger Capacity { get; set; }
}

[Function("recordServiceVerification")]
public sealed class RecordServiceVerificationFunction : FunctionMessage
{
    [Parameter("address", "node", 1)] public string Node { get; set; } = "";
    [Parameter("uint8", "service", 2)] public byte Service { get; set; }
    [Parameter("bool", "passed", 3)] public bool Passed { get; set; }
    [Parameter("uint256", "epoch", 4)] public BigInteger Epoch { get; set; }
}

/// <summary><c>INodeRegistry.NodeStatus</c> ordinals.</summary>
public enum ServiceStatus : byte
{
    Unregistered,
    PendingVerification,
    Active,
    Suspended,
    Banned,
    Deregistering,
}
