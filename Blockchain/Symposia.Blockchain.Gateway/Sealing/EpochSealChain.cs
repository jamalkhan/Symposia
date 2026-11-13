using Symposia.Blockchain.Contracts;
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

    /// <summary>
    /// #227: commits, then reveals, the current epoch's Verifier seed (the commit first: syncing only affects later
    /// epochs), then syncs nodes whose pool membership is stale. Returns a problem worth alerting on (still unseeded
    /// past mid-epoch), else null. No-op without VerifierAssignments.
    /// </summary>
    Task<string?> PrepareVerifierEpochAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>#227: runs <c>finalize</c> chunks until the epoch's Verifier results are final, which the seal waits for.</summary>
    Task FinalizeVerifiersAsync(ulong epoch, CancellationToken ct) => Task.CompletedTask;
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

    private readonly string _epochs;
    private readonly BootstrapChainClient? _nodes;
    private readonly Rewards.VerifierAssignmentsResolver? _resolver;
    private ulong? _synced;

    /// <summary>Targets judged per finalize tx: about 45k gas each, so 100 is about 4.5M (#227 AC6).</summary>
    public const int FinalizeChunk = 100;

    /// <summary>Nodes synced per tx.</summary>
    public const int SyncChunk = 100;

    public EpochSealChain(IOptions<GatewayOptions> options, RelayerAccount relayer, BootstrapChainClient? nodes = null,
        Rewards.VerifierAssignmentsResolver? resolver = null)
    {
        var o = options.Value;
        _relayer = relayer;
        _nodes = nodes;
        _resolver = resolver;
        _epochs = o.EpochControllerAddress;
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

    public async Task<IReadOnlyCollection<string>> RecordNodesAsync(ulong epoch, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_telemetry))
        {
            return [];
        }

        // #165: no record for the epoch exists before its first chunk's block.
        var first = await FirstBlockAsync<FirstRecordBlockFunction>(_telemetry, new FirstRecordBlockFunction { Epoch = epoch });
        return first == 0
            ? []
            : (await NodesAsync<AggregatedRecordWrittenEventDTO>(_telemetry, epoch, e => e.Node, first, epochIsFirstTopic: true))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<ulong> FirstBlockAsync<TFunction>(string contract, TFunction query)
        where TFunction : FunctionMessage, new() =>
        (ulong)await _web3.Eth.GetContractQueryHandler<TFunction>().QueryAsync<BigInteger>(contract, query);

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
        // #165: every report for the epoch is at or after its first report's block; none means no query.
        var first = await FirstBlockAsync<FirstReportBlockFunction>(_distributor, new FirstReportBlockFunction { Epoch = epoch });
        if (first == 0)
        {
            return reporters;
        }

        foreach (var node in await NodesAsync<MetricReportedEventDTO>(_distributor, epoch, e => e.Node, first))
        {
            reporters.Add(node);
        }

        foreach (var node in await NodesAsync<FactorMetricsReportedEventDTO>(_distributor, epoch, e => e.Node, first))
        {
            reporters.Add(node);
        }

        return reporters;
    }

    public async Task SealAsync(ulong epoch, IReadOnlyList<string> nodes, CancellationToken ct)
    {
        var receipt = await _relayer.SendAsync(_distributor, new SealEpochFunction { Epoch = epoch, Nodes = [.. nodes] }, ct);
        // A mined revert (e.g. another sealer won the race) must not count as a seal.
        if (receipt.Status?.Value != 1)
        {
            throw new InvalidOperationException($"sealEpoch({epoch}) reverted in {receipt.TransactionHash}.");
        }
    }

    private async Task<string> AssignmentsAsync() =>
        _resolver is not null && await _resolver.ResolveAsync() is { } r ? r.Address : "";

    public async Task<string?> PrepareVerifierEpochAsync(CancellationToken ct)
    {
        var assignments = await AssignmentsAsync();
        if (assignments.Length == 0 || string.IsNullOrEmpty(_epochs))
        {
            return null;
        }

        var current = (ulong)await _web3.Eth.GetContractQueryHandler<CurrentEpochFunction>().QueryAsync<BigInteger>(_epochs, new CurrentEpochFunction());
        var seed = await _web3.Eth.GetContractQueryHandler<SeedOfFunction>().QueryAsync<byte[]>(assignments, new SeedOfFunction { Epoch = current });
        if (seed.All(b => b == 0))
        {
            var commit = (ulong)await _web3.Eth.GetContractQueryHandler<CommitBlockOfFunction>().QueryAsync<ulong>(assignments, new CommitBlockOfFunction { Epoch = current });
            var head = (ulong)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
            if (commit == 0 || head > commit + 256)
            {
                await Ensure(await _relayer.SendAsync(assignments, new SeedEpochFunction { Epoch = current }, ct), $"seedEpoch({current})");
            }
            else if (head > commit)
            {
                await Ensure(await _relayer.SendAsync(assignments, new RevealSeedFunction { Epoch = current }, ct), $"revealSeed({current})");
            }
        }

        if (_synced != current && _nodes is not null)
        {
            await SyncStaleAsync(assignments, ct);
            _synced = current;
        }

        seed = await _web3.Eth.GetContractQueryHandler<SeedOfFunction>().QueryAsync<byte[]>(assignments, new SeedOfFunction { Epoch = current });
        var start = (long)await _web3.Eth.GetContractQueryHandler<EpochStartFunction>().QueryAsync<BigInteger>(_epochs, new EpochStartFunction { Epoch = current });
        var next = (long)await _web3.Eth.GetContractQueryHandler<EpochStartFunction>().QueryAsync<BigInteger>(_epochs, new EpochStartFunction { Epoch = current + 1 });
        return seed.All(b => b == 0) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > start + ((next - start) / 2)
            ? $"Epoch {current} is past its midpoint and its Verifier seed isn't set: Verifiers will be scored on uptime alone."
            : null;
    }

    /// <summary>Syncs only nodes whose pool membership no longer matches the registry: registered nodes plus current
    /// pool members, so nodes the registry no longer lists are synced out too.</summary>
    private async Task SyncStaleAsync(string assignments, CancellationToken ct)
    {
        var candidates = new HashSet<string>((await _nodes!.ListNodesAsync()).Select(n => n.Node), StringComparer.OrdinalIgnoreCase);
        foreach (byte pool in new byte[] { 5, 8 })
        {
            for (var offset = 0; ; offset += 500)
            {
                var members = await _web3.Eth.GetContractQueryHandler<PoolMembersFunction>().QueryAsync<List<string>>(assignments,
                    new PoolMembersFunction { Pool = pool, Offset = offset, Limit = 500 });
                candidates.UnionWith(members);
                if (members.Count < 500)
                {
                    break;
                }
            }
        }

        foreach (var chunk in candidates.Chunk(SyncChunk))
        {
            var stale = await _web3.Eth.GetContractQueryHandler<StaleAmongFunction>().QueryAsync<List<string>>(assignments, new StaleAmongFunction { Nodes = [.. chunk] });
            if (stale.Count > 0)
            {
                await Ensure(await _relayer.SendAsync(assignments, new SyncManyFunction { Nodes = stale }, ct), "syncMany");
            }
        }
    }

    public async Task FinalizeVerifiersAsync(ulong epoch, CancellationToken ct)
    {
        var assignments = await AssignmentsAsync();
        if (assignments.Length == 0)
        {
            return;
        }

        var finalized = _web3.Eth.GetContractQueryHandler<IsVerifierEpochFinalizedFunction>();
        while (!ct.IsCancellationRequested && !await finalized.QueryAsync<bool>(assignments, new IsVerifierEpochFinalizedFunction { Epoch = epoch }))
        {
            await Ensure(await _relayer.SendAsync(assignments, new FinalizeVerifierEpochFunction { Epoch = epoch, MaxTargets = FinalizeChunk }, ct),
                $"finalize({epoch})");
        }
    }

    private static Task Ensure(TransactionReceipt receipt, string what) => receipt.Status?.Value == 1
        ? Task.CompletedTask
        : throw new InvalidOperationException($"{what} reverted in {receipt.TransactionHash}.");

    /// <summary>Log queries are paged so a long chain never exceeds a provider's block-range or result cap.</summary>
    private const ulong LogPageBlocks = 5_000;

    /// <param name="epochIsFirstTopic">Report events index (node, epoch); record events index (epoch, node).</param>
    /// <param name="fromBlock">The epoch's first report/record block (#165); never earlier than <c>EpochSealer:FromBlock</c>.</param>
    private async Task<IEnumerable<string>> NodesAsync<TEvent>(string contract, ulong epoch, Func<TEvent, string> node, ulong fromBlock,
        bool epochIsFirstTopic = false)
        where TEvent : IEventDTO, new()
    {
        var ev = _web3.Eth.GetEvent<TEvent>(contract);
        var latest = (ulong)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
        var floor = _fromBlock.BlockNumber is { } start ? (ulong)start.Value : 0UL;
        var from = Math.Max(floor, fromBlock);
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
