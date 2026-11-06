using System.Net.Http.Json;
using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace Symposia.Network.Node.Telemetry.Committee;

/// <summary>Where the telemetry contracts and the relay live.</summary>
public sealed class TelemetryChainOptions
{
    public string RpcUrl { get; set; } = "";

    /// <summary>TelemetryCheckpointRegistry (also the EIP-712 verifying contract).</summary>
    public string TelemetryRegistryAddress { get; set; } = "";

    public string EpochControllerAddress { get; set; } = "";

    /// <summary>First block searched for self-checkpoint events (the registry's deployment block).</summary>
    public ulong FromBlock { get; set; }
}

/// <summary>Read-only <see cref="ITelemetryChain"/> over JSON-RPC; never signs or spends gas.</summary>
public sealed class NethereumTelemetryChain(TelemetryChainOptions options, long cadenceSeconds = 300) : ITelemetryChain
{
    private readonly Web3 _web3 = new(options.RpcUrl);

    public async Task<long> NowAsync(CancellationToken ct)
    {
        var block = await _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(BlockParameter.CreateLatest());
        return (long)block.Timestamp.Value;
    }

    public async Task<TelemetrySchedule> ScheduleAsync(CancellationToken ct)
    {
        // Single-length schedule: an EpochController length change takes effect at a boundary and
        // is picked up on the next pass; epochs straddling a change aren't supported off-chain yet.
        var genesis = (long)await Uint<EpochStartFunction>(options.EpochControllerAddress, new EpochStartFunction { Epoch = 0 });
        var length = (long)await Uint<EpochLengthFunction>(options.EpochControllerAddress, new EpochLengthFunction());
        var perEpoch = (int)await Uint<CheckpointsPerEpochFunction>(options.TelemetryRegistryAddress, new CheckpointsPerEpochFunction());
        return new TelemetrySchedule(genesis, length, cadenceSeconds, perEpoch);
    }

    public async Task<(IReadOnlyList<string> Members, int Threshold)> CommitteeAsync(CancellationToken ct)
    {
        var result = await _web3.Eth.GetContractQueryHandler<CommitteeFunction>()
            .QueryDeserializingToObjectAsync<CommitteeOutput>(new CommitteeFunction(), options.TelemetryRegistryAddress);
        return (result.Members, (int)result.Threshold);
    }

    public async Task<long> TelemetryDeadlineAsync(ulong epoch, CancellationToken ct) =>
        (long)await Uint<TelemetryDeadlineFunction>(options.TelemetryRegistryAddress, new TelemetryDeadlineFunction { Epoch = epoch });

    public async Task<long> SelfCheckpointGraceAsync(CancellationToken ct)
    {
        // Mirrors the registry's own check: windowEnd + TELEMETRY_SELF_CHECKPOINT_GRACE.
        var config = await _web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(options.TelemetryRegistryAddress, new ConfigFunction());
        return (long)await Uint<GetUintFunction>(config, new GetUintFunction { Key = SelfCheckpointGraceKey });
    }

    public async Task<CommittedCheckpoint?> CheckpointAsync(ulong epoch, ushort checkpoint, CancellationToken ct) =>
        ToCommitted(await _web3.Eth.GetContractQueryHandler<GetCheckpointFunction>()
            .QueryDeserializingToObjectAsync<CheckpointOutput>(new GetCheckpointFunction { Epoch = epoch, Index = checkpoint }, options.TelemetryRegistryAddress));

    public async Task<CommittedCheckpoint?> SelfCheckpointAsync(ulong epoch, ushort checkpoint, string node, CancellationToken ct) =>
        ToCommitted(await _web3.Eth.GetContractQueryHandler<GetSelfCheckpointFunction>()
            .QueryDeserializingToObjectAsync<CheckpointOutput>(
                new GetSelfCheckpointFunction { Epoch = epoch, Index = checkpoint, Node = node }, options.TelemetryRegistryAddress));

    public async Task<IReadOnlyList<SelfCheckpointRef>> SelfCheckpointsAsync(ulong epoch, CancellationToken ct)
    {
        var ev = _web3.Eth.GetEvent<SelfCheckpointCommittedEvent>(options.TelemetryRegistryAddress);
        var filter = ev.CreateFilterInput<BigInteger>([epoch], new BlockParameter(options.FromBlock), BlockParameter.CreateLatest());
        var logs = await ev.GetAllChangesAsync(filter);
        return [.. logs.Select(l => new SelfCheckpointRef((ulong)l.Event.Epoch, (ushort)l.Event.CheckpointIndex, l.Event.Node,
            l.Event.MerkleRoot, l.Event.BlobAddress))];
    }

    public async Task<int> RecordsPerTxAsync(CancellationToken ct) =>
        (int)await Uint<RecordsPerTxFunction>(options.TelemetryRegistryAddress, new RecordsPerTxFunction());

    public Task<bool> RecordChunkWrittenAsync(ulong epoch, int chunk, CancellationToken ct) =>
        _web3.Eth.GetContractQueryHandler<RecordChunkWrittenFunction>()
            .QueryAsync<bool>(options.TelemetryRegistryAddress, new RecordChunkWrittenFunction { Epoch = epoch, Chunk = chunk });

    public Task<bool> RecordsFinalizedAsync(ulong epoch, CancellationToken ct) =>
        _web3.Eth.GetContractQueryHandler<RecordsFinalizedFunction>()
            .QueryAsync<bool>(options.TelemetryRegistryAddress, new RecordsFinalizedFunction { Epoch = epoch });

    private static readonly byte[] SelfCheckpointGraceKey =
        Nethereum.Util.Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("telemetry.selfCheckpointGrace"));

    private Task<BigInteger> Uint<TFunction>(string address, TFunction function) where TFunction : FunctionMessage, new() =>
        _web3.Eth.GetContractQueryHandler<TFunction>().QueryAsync<BigInteger>(address, function);

    private static CommittedCheckpoint? ToCommitted(CheckpointOutput c) =>
        c.CommittedAt == 0 ? null : new CommittedCheckpoint(c.MerkleRoot, c.BlobAddress, c.SampleCount);

    [Function("epochStart", "uint256")]
    private sealed class EpochStartFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

    [Function("epochLength", "uint256")]
    private sealed class EpochLengthFunction : FunctionMessage;

    [Function("checkpointsPerEpoch", "uint256")]
    private sealed class CheckpointsPerEpochFunction : FunctionMessage;

    [Function("recordsPerTx", "uint256")]
    private sealed class RecordsPerTxFunction : FunctionMessage;

    [Function("telemetryDeadline", "uint256")]
    private sealed class TelemetryDeadlineFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

    [Function("config", "address")]
    private sealed class ConfigFunction : FunctionMessage;

    [Function("getUint", "uint256")]
    private sealed class GetUintFunction : FunctionMessage { [Parameter("bytes32", "key", 1)] public byte[] Key { get; set; } = []; }

    [Function("committee")]
    private sealed class CommitteeFunction : FunctionMessage;

    [FunctionOutput]
    private sealed class CommitteeOutput : IFunctionOutputDTO
    {
        [Parameter("address[]", "members", 1)] public List<string> Members { get; set; } = [];
        [Parameter("uint256", "threshold_", 2)] public BigInteger Threshold { get; set; }
    }

    [Function("getCheckpoint")]
    private sealed class GetCheckpointFunction : FunctionMessage
    {
        [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
        [Parameter("uint256", "checkpointIndex", 2)] public BigInteger Index { get; set; }
    }

    [Function("getSelfCheckpoint")]
    private sealed class GetSelfCheckpointFunction : FunctionMessage
    {
        [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
        [Parameter("uint256", "checkpointIndex", 2)] public BigInteger Index { get; set; }
        [Parameter("address", "node", 3)] public string Node { get; set; } = "";
    }

    [FunctionOutput]
    private sealed class CheckpointOutput : IFunctionOutputDTO
    {
        [Parameter("bytes32", "merkleRoot", 1)] public byte[] MerkleRoot { get; set; } = [];
        [Parameter("bytes32", "blobAddress", 2)] public byte[] BlobAddress { get; set; } = [];
        [Parameter("uint32", "sampleCount", 3)] public uint SampleCount { get; set; }
        [Parameter("uint64", "committedAt", 4)] public ulong CommittedAt { get; set; }
    }

    [Function("recordChunkWritten", "bool")]
    private sealed class RecordChunkWrittenFunction : FunctionMessage
    {
        [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
        [Parameter("uint256", "chunkIndex", 2)] public BigInteger Chunk { get; set; }
    }

    [Function("recordsFinalized", "bool")]
    private sealed class RecordsFinalizedFunction : FunctionMessage { [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; } }

    [Event("SelfCheckpointCommitted")]
    private sealed class SelfCheckpointCommittedEvent : IEventDTO
    {
        [Parameter("uint256", "epoch", 1, true)] public BigInteger Epoch { get; set; }
        [Parameter("uint256", "checkpointIndex", 2, true)] public BigInteger CheckpointIndex { get; set; }
        [Parameter("address", "node", 3, true)] public string Node { get; set; } = "";
        [Parameter("bytes32", "merkleRoot", 4, false)] public byte[] MerkleRoot { get; set; } = [];
        [Parameter("bytes32", "blobAddress", 5, false)] public byte[] BlobAddress { get; set; } = [];
    }
}

/// <summary>Wire shapes of the Gateway's telemetry relay endpoints (#158 FR5); shared by client and server.</summary>
public static class TelemetryRelayWire
{
    public const string CheckpointsPath = "/v1/telemetry/checkpoints";
    public const string SelfCheckpointsPath = "/v1/telemetry/self-checkpoints";
    public const string RecordsPath = "/v1/telemetry/records";

    public sealed record CheckpointRequest(ulong Epoch, ushort Checkpoint, string MerkleRoot, string BlobAddress, uint SampleCount, IReadOnlyList<string> Signatures);

    public sealed record SelfCheckpointRequest(string Node, ulong Epoch, ushort Checkpoint, string MerkleRoot, string BlobAddress, uint SampleCount,
        string Signature, string Batch);

    public sealed record RecordsRequest(ulong Epoch, int ChunkIndex, int ChunkCount, IReadOnlyList<RecordWire> Records, IReadOnlyList<string> Signatures);

    public sealed record MetricWire(uint UptimeBps, ulong ReadThroughputKBps, ulong WriteThroughputKBps, ulong BandwidthInKbps, ulong BandwidthOutKbps,
        uint LatencyMs, uint RetrievalMs)
    {
        public static MetricWire From(MetricValuesInput v) =>
            new(v.UptimeBps, v.ReadThroughputKBps, v.WriteThroughputKBps, v.BandwidthInKbps, v.BandwidthOutKbps, v.LatencyMs, v.RetrievalMs);

        public MetricValuesInput ToInput() => new()
        {
            UptimeBps = UptimeBps, ReadThroughputKBps = ReadThroughputKBps, WriteThroughputKBps = WriteThroughputKBps,
            BandwidthInKbps = BandwidthInKbps, BandwidthOutKbps = BandwidthOutKbps, LatencyMs = LatencyMs, RetrievalMs = RetrievalMs,
        };
    }

    public sealed record RecordWire(string Node, MetricWire SelfReported, MetricWire VerifierAttested, uint HeartbeatsReceived,
        uint HeartbeatsExpected, ushort AttestationCount, ushort FirstCheckpoint, ushort LastCheckpoint)
    {
        public static RecordWire From(AggregatedMetricInput r) => new(r.Node, MetricWire.From(r.SelfReported), MetricWire.From(r.VerifierAttested),
            r.HeartbeatsReceived, r.HeartbeatsExpected, r.AttestationCount, r.FirstCheckpoint, r.LastCheckpoint);

        public AggregatedMetricInput ToInput() => new()
        {
            Node = Node, SelfReported = SelfReported.ToInput(), VerifierAttested = VerifierAttested.ToInput(),
            HeartbeatsReceived = HeartbeatsReceived, HeartbeatsExpected = HeartbeatsExpected, AttestationCount = AttestationCount,
            FirstCheckpoint = FirstCheckpoint, LastCheckpoint = LastCheckpoint,
        };
    }
}

/// <summary><see cref="ITelemetryRelay"/> over the Blockchain Gateway's HTTP relay endpoints.</summary>
public sealed class HttpTelemetryRelay(HttpClient http) : ITelemetryRelay
{
    public Task CommitCheckpointAsync(ulong epoch, ushort checkpoint, byte[] merkleRoot, byte[] blobAddress, uint sampleCount,
        IReadOnlyList<string> signatures, CancellationToken ct) =>
        PostAsync(TelemetryRelayWire.CheckpointsPath,
            new TelemetryRelayWire.CheckpointRequest(epoch, checkpoint, merkleRoot.ToHex(true), blobAddress.ToHex(true), sampleCount, signatures), ct);

    public Task SelfCheckpointAsync(string node, ulong epoch, ushort checkpoint, byte[] merkleRoot, byte[] blobAddress, uint sampleCount,
        string signature, byte[] batch, CancellationToken ct) =>
        PostAsync(TelemetryRelayWire.SelfCheckpointsPath,
            new TelemetryRelayWire.SelfCheckpointRequest(node, epoch, checkpoint, merkleRoot.ToHex(true), blobAddress.ToHex(true), sampleCount,
                signature, Convert.ToBase64String(batch)), ct);

    public Task SubmitRecordsAsync(ulong epoch, int chunkIndex, int chunkCount, IReadOnlyList<AggregatedMetricInput> records,
        IReadOnlyList<string> signatures, CancellationToken ct) =>
        PostAsync(TelemetryRelayWire.RecordsPath,
            new TelemetryRelayWire.RecordsRequest(epoch, chunkIndex, chunkCount, [.. records.Select(TelemetryRelayWire.RecordWire.From)], signatures), ct);

    private async Task PostAsync<T>(string path, T body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Relay {path} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        }
    }
}
