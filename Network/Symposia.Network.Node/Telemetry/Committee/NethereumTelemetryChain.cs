using Symposia.Blockchain.Contracts;
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

    /// <summary>Widest block span per <c>eth_getLogs</c>, to fit capped RPCs like the gateway proxy (#172).</summary>
    public ulong MaxLogBlockRange { get; set; } = LogPager.DefaultWindow;
}

/// <summary>Read-only <see cref="ITelemetryChain"/> over JSON-RPC; never signs or spends gas.</summary>
public sealed class NethereumTelemetryChain(TelemetryChainOptions options, long cadenceSeconds = 300, HttpClient? http = null) : ITelemetryChain
{
    // Per-epoch scan progress (#172 review): mined logs don't change, so each pass only reads blocks
    // past the last one scanned instead of rescanning from FromBlock every 15s.
    private readonly Dictionary<ulong, (ulong Next, List<SelfCheckpointRef> Found)> _selfCheckpointScans = [];
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private readonly Web3 _web3 = http is null ? new(options.RpcUrl) : new(new Nethereum.JsonRpc.Client.RpcClient(new Uri(options.RpcUrl), http));

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
        await _scanGate.WaitAsync(ct);
        try
        {
            var latest = (ulong)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
            var (next, found) = _selfCheckpointScans.GetValueOrDefault(epoch, (options.FromBlock, []));
            var logs = await LogPager.ScanAsync(next, latest, options.MaxLogBlockRange, async (from, to) =>
            {
                ct.ThrowIfCancellationRequested();
                return (IEnumerable<EventLog<SelfCheckpointCommittedEvent>>)await ev.GetAllChangesAsync(
                    ev.CreateFilterInput<BigInteger>([epoch], new BlockParameter(from), new BlockParameter(to)));
            });
            found = [.. found, .. logs.Select(l => new SelfCheckpointRef((ulong)l.Event.Epoch, (ushort)l.Event.CheckpointIndex, l.Event.Node,
                l.Event.MerkleRoot, l.Event.BlobAddress))];
            _selfCheckpointScans[epoch] = (Math.Max(next, latest + 1), found);
            foreach (var old in _selfCheckpointScans.Keys.Where(e => e + 4 < epoch).ToList())
            {
                _selfCheckpointScans.Remove(old); // finished epochs aren't asked for again
            }

            return found;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public async Task<int> RecordsPerTxAsync(CancellationToken ct) =>
        (int)await Uint<RecordsPerTxFunction>(options.TelemetryRegistryAddress, new RecordsPerTxFunction());

    public Task<bool> RecordChunkWrittenAsync(ulong epoch, int chunk, CancellationToken ct) =>
        _web3.Eth.GetContractQueryHandler<RecordChunkWrittenFunction>()
            .QueryAsync<bool>(options.TelemetryRegistryAddress, new RecordChunkWrittenFunction { Epoch = epoch, Chunk = chunk });

    public async Task<int> RecordChunkCountAsync(ulong epoch, CancellationToken ct) =>
        (int)await Uint<RecordChunkCountFunction>(options.TelemetryRegistryAddress, new RecordChunkCountFunction { Epoch = epoch });

    public Task<bool> RecordsFinalizedAsync(ulong epoch, CancellationToken ct) =>
        _web3.Eth.GetContractQueryHandler<RecordsFinalizedFunction>()
            .QueryAsync<bool>(options.TelemetryRegistryAddress, new RecordsFinalizedFunction { Epoch = epoch });

    private static readonly byte[] SelfCheckpointGraceKey =
        Nethereum.Util.Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("telemetry.selfCheckpointGrace"));

    private Task<BigInteger> Uint<TFunction>(string address, TFunction function) where TFunction : FunctionMessage, new() =>
        _web3.Eth.GetContractQueryHandler<TFunction>().QueryAsync<BigInteger>(address, function);

    private static CommittedCheckpoint? ToCommitted(CheckpointOutput c) =>
        c.CommittedAt == 0 ? null : new CommittedCheckpoint(c.MerkleRoot, c.BlobAddress, c.SampleCount);

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
        uint HeartbeatsExpected, ushort AttestationCount, ushort FirstCheckpoint, ushort LastCheckpoint, ulong AvailableBytes = 0, ulong UsedBytes = 0)
    {
        public static RecordWire From(AggregatedMetricInput r) => new(r.Node, MetricWire.From(r.SelfReported), MetricWire.From(r.VerifierAttested),
            r.HeartbeatsReceived, r.HeartbeatsExpected, r.AttestationCount, r.FirstCheckpoint, r.LastCheckpoint, r.AvailableBytes, r.UsedBytes);

        public AggregatedMetricInput ToInput() => new()
        {
            Node = Node, SelfReported = SelfReported.ToInput(), VerifierAttested = VerifierAttested.ToInput(),
            HeartbeatsReceived = HeartbeatsReceived, HeartbeatsExpected = HeartbeatsExpected, AttestationCount = AttestationCount,
            FirstCheckpoint = FirstCheckpoint, LastCheckpoint = LastCheckpoint, AvailableBytes = AvailableBytes, UsedBytes = UsedBytes,
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
