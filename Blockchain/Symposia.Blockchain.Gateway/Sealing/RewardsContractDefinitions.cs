using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Gateway.Sealing;

// RewardDistributor (#80) and EpochController (#72) ABI surface used by the epoch sealer.

[Function("nextSealableEpoch", "uint256")]
public sealed class NextSealableEpochFunction : FunctionMessage;

[Function("reporterCount", "uint256")]
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

[Function("recordCount", "uint256")]
public sealed class RecordCountFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)]
    public BigInteger Epoch { get; set; }
}

[Event("AggregatedRecordWritten")]
public sealed class AggregatedRecordWrittenEventDTO : IEventDTO
{
    [Parameter("uint256", "epoch", 1, true)]
    public BigInteger Epoch { get; set; }

    [Parameter("address", "node", 2, true)]
    public string Node { get; set; } = "";

    [Parameter("uint16", "heartbeatComplianceBps", 3, false)]
    public ushort HeartbeatComplianceBps { get; set; }

    [Parameter("bool", "eligible", 4, false)]
    public bool Eligible { get; set; }

    [Parameter("bool", "divergenceFlag", 5, false)]
    public bool DivergenceFlag { get; set; }
}
