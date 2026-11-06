using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Contracts;

// RewardDistributor bindings (#167): the one copy every Symposia .NET component uses; ContractAbiTests pins them to the forge ABI.

/// <summary>#165: block of an epoch's first metric report (0 = none).</summary>
[Function("firstReportBlock", "uint256")]
public sealed class FirstReportBlockFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)]
    public BigInteger Epoch { get; set; }
}

[Function("nextSealableEpoch", "uint256")]
public sealed class NextSealableEpochFunction : FunctionMessage;

/// <summary>Distinct reporting nodes (#159); <c>reporterCount</c> counts per-service reward accounts.</summary>
[Function("reportingNodeCount", "uint256")]
public sealed class ReporterCountFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)]
    public BigInteger Epoch { get; set; }
}

[Function("sealEpoch")]
public sealed class SealEpochFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)]
    public BigInteger Epoch { get; set; }

    [Parameter("address[]", "nodes", 2)]
    public List<string> Nodes { get; set; } = [];
}

[Function("isSealed", "bool")]
public sealed class IsEpochSealedFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)]
    public BigInteger Epoch { get; set; }
}

[Event("MetricReported")]
public sealed class MetricReportedEventDTO : IEventDTO
{
    [Parameter("address", "node", 1, true)]
    public string Node { get; set; } = "";

    [Parameter("uint256", "epoch", 2, true)]
    public BigInteger Epoch { get; set; }

    [Parameter("uint256", "score", 3, false)]
    public BigInteger Score { get; set; }

    [Parameter("uint256", "heartbeatBps", 4, false)]
    public BigInteger HeartbeatBps { get; set; }
}

[Event("FactorMetricsReported")]
public sealed class FactorMetricsReportedEventDTO : IEventDTO
{
    [Parameter("address", "node", 1, true)]
    public string Node { get; set; } = "";

    [Parameter("uint256", "epoch", 2, true)]
    public BigInteger Epoch { get; set; }

    [Parameter("uint256", "heartbeatBps", 3, false)]
    public BigInteger HeartbeatBps { get; set; }
}

[Function("canSeal", "bool")]
public sealed class CanSealFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)]
    public BigInteger Epoch { get; set; }
}

[Function("epochSource", "uint8")]
public sealed class EpochSourceFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)]
    public BigInteger Epoch { get; set; }
}

[Event("ServiceSettled")]
public sealed class ServiceSettledEvent : IEventDTO
{
    [Parameter("address", "node", 1, true)] public string Node { get; set; } = "";
    [Parameter("uint8", "service", 2, true)] public byte Service { get; set; }
    [Parameter("uint256", "epoch", 3, true)] public BigInteger Epoch { get; set; }
    [Parameter("uint8", "reason", 4, false)] public byte Reason { get; set; }
    [Parameter("address", "account", 5, false)] public string Account { get; set; } = "";
    [Parameter("uint256", "amount", 6, false)] public BigInteger Amount { get; set; }
    [Parameter("uint256", "paid", 7, false)] public BigInteger Paid { get; set; }
    [Parameter("uint256", "held", 8, false)] public BigInteger Held { get; set; }
}

[Event("ServiceReserveClaimed")]
public sealed class ServiceReserveClaimedEvent : IEventDTO
{
    [Parameter("address", "node", 1, true)] public string Node { get; set; } = "";
    [Parameter("uint8", "service", 2, true)] public byte Service { get; set; }
    [Parameter("uint256", "amount", 3, false)] public BigInteger Amount { get; set; }
}

[Function("accountOf", "address")]
public sealed class AccountOfFunction : FunctionMessage
{
    [Parameter("address", "node", 1)] public string Node { get; set; } = "";
    [Parameter("uint8", "service", 2)] public byte Service { get; set; }
}

[Function("reserveBalanceOf", "uint256")]
public sealed class ReserveBalanceOfFunction : FunctionMessage
{
    [Parameter("address", "node", 1)] public string Account { get; set; } = "";
}
