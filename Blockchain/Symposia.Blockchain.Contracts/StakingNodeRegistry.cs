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
