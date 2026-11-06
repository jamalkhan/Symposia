using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Symposia.Blockchain.Gateway.Chain;

namespace Symposia.Blockchain.Gateway.Sealing;

/// <summary>The chain reads and the one write the epoch sealer needs.</summary>
public interface IEpochSealChain
{
    /// <summary>The distributor's next epoch to settle (seals are strictly sequential).</summary>
    Task<ulong> NextSealableEpochAsync(CancellationToken ct);

    /// <summary>
    /// Whether the distributor would accept a seal now: finality has passed and, when the network
    /// has telemetry, the epoch's records are final or the fallback wait has passed (#76).
    /// </summary>
    Task<bool> CanSealAsync(ulong epoch, CancellationToken ct);

    /// <summary>True when the epoch settles from telemetry records, whose nodes must then be included too.</summary>
    Task<bool> SettlesFromTelemetryAsync(ulong epoch, CancellationToken ct);

    /// <summary>Nodes with a telemetry record for the epoch, from <c>AggregatedRecordWritten</c> logs.</summary>
    Task<IReadOnlyCollection<string>> RecordNodesAsync(ulong epoch, CancellationToken ct);

    /// <summary>How many nodes have a telemetry record for the epoch, per the registry.</summary>
    Task<ulong> RecordCountAsync(ulong epoch, CancellationToken ct);

    /// <summary>How many distinct nodes reported metrics for the epoch, per the distributor.</summary>
    Task<ulong> ReporterCountAsync(ulong epoch, CancellationToken ct);

    /// <summary>Distinct nodes that reported for the epoch, from the distributor's report events.</summary>
    Task<IReadOnlyCollection<string>> ReportersAsync(ulong epoch, CancellationToken ct);

    Task SealAsync(ulong epoch, IReadOnlyList<string> nodes, CancellationToken ct);
}

/// <summary>
/// Nethereum implementation. Reporters come from <c>MetricReported</c> and
/// <c>FactorMetricsReported</c> logs filtered on the indexed epoch topic, so each lookup is one
/// indexed <c>eth_getLogs</c> per event type rather than a scan of every report.
/// </summary>
public sealed class EpochSealChain : IEpochSealChain
{
    private readonly Web3 _web3;
    private readonly string _distributor;
    private readonly string _telemetry;
    private readonly BlockParameter _fromBlock;

    private readonly RelayerAccount _relayer;

    public EpochSealChain(IOptions<GatewayOptions> options, RelayerAccount relayer)
    {
        var o = options.Value;
        _relayer = relayer;
        _web3 = relayer.Web3;
        _distributor = o.RewardDistributorAddress;
        _telemetry = o.TelemetryRegistryAddress;
        _fromBlock = new BlockParameter(o.EpochSealer.FromBlock);
    }

    public async Task<ulong> NextSealableEpochAsync(CancellationToken ct) =>
        (ulong)await _web3.Eth.GetContractQueryHandler<NextSealableEpochFunction>()
            .QueryAsync<BigInteger>(_distributor, new NextSealableEpochFunction());

    public Task<bool> CanSealAsync(ulong epoch, CancellationToken ct) =>
        _web3.Eth.GetContractQueryHandler<CanSealFunction>().QueryAsync<bool>(_distributor, new CanSealFunction { Epoch = epoch });

    public async Task<bool> SettlesFromTelemetryAsync(ulong epoch, CancellationToken ct) =>
        await _web3.Eth.GetContractQueryHandler<EpochSourceFunction>()
            .QueryAsync<byte>(_distributor, new EpochSourceFunction { Epoch = epoch }) == 1; // EpochSource.Telemetry

    public async Task<IReadOnlyCollection<string>> RecordNodesAsync(ulong epoch, CancellationToken ct) =>
        string.IsNullOrEmpty(_telemetry)
            ? []
            : (await NodesAsync<AggregatedRecordWrittenEventDTO>(_telemetry, epoch, e => e.Node, epochIsFirstTopic: true))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<ulong> RecordCountAsync(ulong epoch, CancellationToken ct) =>
        string.IsNullOrEmpty(_telemetry)
            ? 0
            : (ulong)await _web3.Eth.GetContractQueryHandler<RecordCountFunction>()
                .QueryAsync<BigInteger>(_telemetry, new RecordCountFunction { Epoch = epoch });

    public async Task<ulong> ReporterCountAsync(ulong epoch, CancellationToken ct) =>
        (ulong)await _web3.Eth.GetContractQueryHandler<ReporterCountFunction>()
            .QueryAsync<BigInteger>(_distributor, new ReporterCountFunction { Epoch = epoch });

    public async Task<IReadOnlyCollection<string>> ReportersAsync(ulong epoch, CancellationToken ct)
    {
        var reporters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in await NodesAsync<MetricReportedEventDTO>(_distributor, epoch, e => e.Node))
        {
            reporters.Add(node);
        }

        foreach (var node in await NodesAsync<FactorMetricsReportedEventDTO>(_distributor, epoch, e => e.Node))
        {
            reporters.Add(node);
        }

        return reporters;
    }

    public async Task SealAsync(ulong epoch, IReadOnlyList<string> nodes, CancellationToken ct)
    {
        var receipt = await _relayer.SendAsync(web3 => web3.Eth.GetContractTransactionHandler<SealEpochFunction>()
            .SendRequestAndWaitForReceiptAsync(_distributor, new SealEpochFunction { Epoch = epoch, Nodes = [.. nodes] }, ct), ct);
        // A mined revert (e.g. another sealer won the race) must not count as a seal.
        if (receipt.Status?.Value != 1)
        {
            throw new InvalidOperationException($"sealEpoch({epoch}) reverted in {receipt.TransactionHash}.");
        }
    }

    /// <summary>Log queries are paged so a long chain never exceeds a provider's block-range or result cap.</summary>
    private const ulong LogPageBlocks = 5_000;

    /// <param name="epochIsFirstTopic">Report events index (node, epoch); record events index (epoch, node).</param>
    private async Task<IEnumerable<string>> NodesAsync<TEvent>(string contract, ulong epoch, Func<TEvent, string> node, bool epochIsFirstTopic = false)
        where TEvent : IEventDTO, new()
    {
        var ev = _web3.Eth.GetEvent<TEvent>(contract);
        var latest = (ulong)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
        var from = _fromBlock.BlockNumber is { } start ? (ulong)start.Value : 0UL;
        var nodes = new List<string>();
        for (; from <= latest; from += LogPageBlocks)
        {
            var to = new BlockParameter(new Nethereum.Hex.HexTypes.HexBigInteger(Math.Min(latest, from + LogPageBlocks - 1)));
            var page = new BlockParameter(new Nethereum.Hex.HexTypes.HexBigInteger(from));
            // An empty topic array matches any node; the other indexed topic pins the epoch.
            var filter = epochIsFirstTopic
                ? ev.CreateFilterInput<BigInteger>([epoch], page, to)
                : ev.CreateFilterInput<object, BigInteger>([], [epoch], page, to);
            nodes.AddRange((await ev.GetAllChangesAsync(filter)).Select(l => node(l.Event)));
        }

        return nodes;
    }
}
