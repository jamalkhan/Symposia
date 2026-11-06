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

    /// <summary>Whether the epoch controller says the epoch's finality window has closed.</summary>
    Task<bool> IsEpochFinalAsync(ulong epoch, CancellationToken ct);

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
    private readonly string _epochs;
    private readonly BlockParameter _fromBlock;

    private readonly RelayerAccount _relayer;

    public EpochSealChain(IOptions<GatewayOptions> options, RelayerAccount relayer)
    {
        var o = options.Value;
        _relayer = relayer;
        _web3 = relayer.Web3;
        _distributor = o.RewardDistributorAddress;
        _epochs = o.EpochControllerAddress;
        _fromBlock = new BlockParameter(o.EpochSealer.FromBlock);
    }

    public async Task<ulong> NextSealableEpochAsync(CancellationToken ct) =>
        (ulong)await _web3.Eth.GetContractQueryHandler<NextSealableEpochFunction>()
            .QueryAsync<BigInteger>(_distributor, new NextSealableEpochFunction());

    public Task<bool> IsEpochFinalAsync(ulong epoch, CancellationToken ct) =>
        _web3.Eth.GetContractQueryHandler<IsEpochSealedFunction>()
            .QueryAsync<bool>(_epochs, new IsEpochSealedFunction { Epoch = epoch });

    public async Task<ulong> ReporterCountAsync(ulong epoch, CancellationToken ct) =>
        (ulong)await _web3.Eth.GetContractQueryHandler<ReporterCountFunction>()
            .QueryAsync<BigInteger>(_distributor, new ReporterCountFunction { Epoch = epoch });

    public async Task<IReadOnlyCollection<string>> ReportersAsync(ulong epoch, CancellationToken ct)
    {
        var reporters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in await NodesAsync<MetricReportedEventDTO>(epoch, e => e.Node))
        {
            reporters.Add(node);
        }

        foreach (var node in await NodesAsync<FactorMetricsReportedEventDTO>(epoch, e => e.Node))
        {
            reporters.Add(node);
        }

        return reporters;
    }

    public Task SealAsync(ulong epoch, IReadOnlyList<string> nodes, CancellationToken ct) =>
        _relayer.SendAsync(web3 => web3.Eth.GetContractTransactionHandler<SealEpochFunction>()
            .SendRequestAndWaitForReceiptAsync(_distributor, new SealEpochFunction { Epoch = epoch, Nodes = [.. nodes] }, ct), ct);

    private async Task<IEnumerable<string>> NodesAsync<TEvent>(ulong epoch, Func<TEvent, string> node)
        where TEvent : IEventDTO, new()
    {
        var ev = _web3.Eth.GetEvent<TEvent>(_distributor);
        // Empty first-topic array = any node; second topic pins the epoch.
        var filter = ev.CreateFilterInput<object, BigInteger>([], [epoch], _fromBlock, BlockParameter.CreateLatest());
        var logs = await ev.GetAllChangesAsync(filter);
        return logs.Select(l => node(l.Event));
    }
}
