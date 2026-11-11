using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace Symposia.Blockchain.Contracts;

// TelemetryCheckpointRegistry bindings (#167): the one copy every Symposia .NET component uses; ContractAbiTests pins them to the forge ABI.

/// <summary>#165: block of an epoch's first records chunk (0 = none).</summary>
[Function("firstRecordBlock", "uint256")]
public sealed class FirstRecordBlockFunction : FunctionMessage
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

[Function("commitCheckpoint")]
public sealed class CommitCheckpointFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
    [Parameter("uint256", "checkpointIndex", 2)] public BigInteger Index { get; set; }
    [Parameter("bytes32", "merkleRoot", 3)] public byte[] MerkleRoot { get; set; } = [];
    [Parameter("bytes32", "blobAddress", 4)] public byte[] BlobAddress { get; set; } = [];
    [Parameter("uint32", "sampleCount", 5)] public uint SampleCount { get; set; }
    [Parameter("bytes[]", "signatures", 6)] public List<byte[]> Signatures { get; set; } = [];
}

[Function("selfCheckpointFor")]
public sealed class SelfCheckpointForFunction : FunctionMessage
{
    [Parameter("address", "node", 1)] public string Node { get; set; } = "";
    [Parameter("uint256", "epoch", 2)] public BigInteger Epoch { get; set; }
    [Parameter("uint256", "checkpointIndex", 3)] public BigInteger Index { get; set; }
    [Parameter("bytes32", "merkleRoot", 4)] public byte[] MerkleRoot { get; set; } = [];
    [Parameter("bytes32", "blobAddress", 5)] public byte[] BlobAddress { get; set; } = [];
    [Parameter("uint32", "sampleCount", 6)] public uint SampleCount { get; set; }
    [Parameter("bytes", "signature", 7)] public byte[] Signature { get; set; } = [];
}

[Function("submitAggregatedRecords")]
public sealed class SubmitAggregatedRecordsFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
    [Parameter("uint256", "chunkIndex", 2)] public BigInteger ChunkIndex { get; set; }
    [Parameter("uint256", "chunkCount", 3)] public BigInteger ChunkCount { get; set; }
    [Parameter("tuple[]", "inputs", 4, structTypeName: "AggregatedMetricInput[]")] public List<AggregatedMetricInput> Inputs { get; set; } = [];
    [Parameter("bytes[]", "signatures", 5)] public List<byte[]> Signatures { get; set; } = [];
}

[Function("checkpointsPerEpoch", "uint256")]
public sealed class CheckpointsPerEpochFunction : FunctionMessage;

[Function("recordsPerTx", "uint256")]
public sealed class RecordsPerTxFunction : FunctionMessage;

[Function("telemetryDeadline", "uint256")]
public sealed class TelemetryDeadlineFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

[Function("committee")]
public sealed class CommitteeFunction : FunctionMessage;

[FunctionOutput]
public sealed class CommitteeOutput : IFunctionOutputDTO
{
    [Parameter("address[]", "members", 1)] public List<string> Members { get; set; } = [];
    [Parameter("uint256", "threshold_", 2)] public BigInteger Threshold { get; set; }
}

[Function("getCheckpoint")]
public sealed class GetCheckpointFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
    [Parameter("uint256", "checkpointIndex", 2)] public BigInteger Index { get; set; }
}

[Function("getSelfCheckpoint")]
public sealed class GetSelfCheckpointFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
    [Parameter("uint256", "checkpointIndex", 2)] public BigInteger Index { get; set; }
    [Parameter("address", "node", 3)] public string Node { get; set; } = "";
}

[FunctionOutput]
public sealed class CheckpointOutput : IFunctionOutputDTO
{
    [Parameter("bytes32", "merkleRoot", 1)] public byte[] MerkleRoot { get; set; } = [];
    [Parameter("bytes32", "blobAddress", 2)] public byte[] BlobAddress { get; set; } = [];
    [Parameter("uint32", "sampleCount", 3)] public uint SampleCount { get; set; }
    [Parameter("uint64", "committedAt", 4)] public ulong CommittedAt { get; set; }
}

[Function("recordChunkCount", "uint256")]
public sealed class RecordChunkCountFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)]
    public BigInteger Epoch { get; set; }
}

[Function("recordChunkWritten", "bool")]
public sealed class RecordChunkWrittenFunction : FunctionMessage
{
    [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
    [Parameter("uint256", "chunkIndex", 2)] public BigInteger Chunk { get; set; }
}

[Function("recordsFinalized", "bool")]
public sealed class RecordsFinalizedFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

[Event("SelfCheckpointCommitted")]
public sealed class SelfCheckpointCommittedEvent : IEventDTO
{
    [Parameter("uint256", "epoch", 1, true)] public BigInteger Epoch { get; set; }
    [Parameter("uint256", "checkpointIndex", 2, true)] public BigInteger CheckpointIndex { get; set; }
    [Parameter("address", "node", 3, true)] public string Node { get; set; } = "";
    [Parameter("bytes32", "merkleRoot", 4, false)] public byte[] MerkleRoot { get; set; } = [];
    [Parameter("bytes32", "blobAddress", 5, false)] public byte[] BlobAddress { get; set; } = [];
}

/// <summary>ABI mirror of <c>TelemetryCheckpointRegistry.MetricValues</c>.</summary>
[Struct("MetricValues")]
public sealed class MetricValuesInput
{
    [Parameter("uint32", "uptimeBps", 1)] public uint UptimeBps { get; set; }
    [Parameter("uint64", "readThroughputKBps", 2)] public ulong ReadThroughputKBps { get; set; }
    [Parameter("uint64", "writeThroughputKBps", 3)] public ulong WriteThroughputKBps { get; set; }
    [Parameter("uint64", "bandwidthInKbps", 4)] public ulong BandwidthInKbps { get; set; }
    [Parameter("uint64", "bandwidthOutKbps", 5)] public ulong BandwidthOutKbps { get; set; }
    [Parameter("uint32", "latencyMs", 6)] public uint LatencyMs { get; set; }
    [Parameter("uint32", "retrievalMs", 7)] public uint RetrievalMs { get; set; }
}

/// <summary>ABI mirror of <c>TelemetryCheckpointRegistry.AggregatedMetricInput</c>.</summary>
[Struct("AggregatedMetricInput")]
public sealed class AggregatedMetricInput
{
    [Parameter("address", "node", 1)] public string Node { get; set; } = "";
    [Parameter("tuple", "selfReported", 2, structTypeName: "MetricValues")] public MetricValuesInput SelfReported { get; set; } = new();
    [Parameter("tuple", "verifierAttested", 3, structTypeName: "MetricValues")] public MetricValuesInput VerifierAttested { get; set; } = new();
    [Parameter("uint32", "heartbeatsReceived", 4)] public uint HeartbeatsReceived { get; set; }
    [Parameter("uint32", "heartbeatsExpected", 5)] public uint HeartbeatsExpected { get; set; }
    [Parameter("uint16", "attestationCount", 6)] public ushort AttestationCount { get; set; }
    [Parameter("uint16", "firstCheckpoint", 7)] public ushort FirstCheckpoint { get; set; }
    [Parameter("uint16", "lastCheckpoint", 8)] public ushort LastCheckpoint { get; set; }
    [Parameter("uint64", "availableBytes", 9)] public ulong AvailableBytes { get; set; }
    [Parameter("uint64", "usedBytes", 10)] public ulong UsedBytes { get; set; }
}

[Function("getEpochMetricRecord")]
public sealed class GetEpochMetricRecordFunction : FunctionMessage
{
    [Parameter("address", "node", 1)] public string Node { get; set; } = "";

    [Parameter("uint256", "epoch", 2)] public BigInteger Epoch { get; set; }
}

[FunctionOutput]
public sealed class EpochMetricRecordOutput : IFunctionOutputDTO
{
    [Parameter("tuple", "", 1, structTypeName: "AggregatedMetricRecord")] public AggregatedMetricRecord Record { get; set; } = new();
}

/// <summary>ABI mirror of <c>TelemetryCheckpointRegistry.AggregatedMetricRecord</c> (#198 Network Dashboard).</summary>
[Struct("AggregatedMetricRecord")]
public sealed class AggregatedMetricRecord
{
    [Parameter("tuple", "selfReported", 1, structTypeName: "MetricValues")] public MetricValuesInput SelfReported { get; set; } = new();
    [Parameter("tuple", "verifierAttested", 2, structTypeName: "MetricValues")] public MetricValuesInput VerifierAttested { get; set; } = new();
    [Parameter("uint32", "heartbeatsReceived", 3)] public uint HeartbeatsReceived { get; set; }
    [Parameter("uint32", "heartbeatsExpected", 4)] public uint HeartbeatsExpected { get; set; }
    [Parameter("uint16", "heartbeatComplianceBps", 5)] public ushort HeartbeatComplianceBps { get; set; }
    [Parameter("uint16", "attestationCount", 6)] public ushort AttestationCount { get; set; }
    [Parameter("uint16", "firstCheckpoint", 7)] public ushort FirstCheckpoint { get; set; }
    [Parameter("uint16", "lastCheckpoint", 8)] public ushort LastCheckpoint { get; set; }
    [Parameter("bool", "eligible", 9)] public bool Eligible { get; set; }
    [Parameter("bool", "divergenceFlag", 10)] public bool DivergenceFlag { get; set; }
    [Parameter("bool", "exists", 11)] public bool Exists { get; set; }
    [Parameter("uint64", "availableBytes", 12)] public ulong AvailableBytes { get; set; }
    [Parameter("uint64", "usedBytes", 13)] public ulong UsedBytes { get; set; }
}
