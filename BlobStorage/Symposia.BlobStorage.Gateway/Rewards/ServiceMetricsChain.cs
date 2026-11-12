using System.Numerics;
using Nethereum.Util;
using Nethereum.Web3;
using Symposia.Blockchain.Contracts;

namespace Symposia.BlobStorage.Gateway.Rewards;

/// <summary>Where the gateway reads epochs and the metric signer (#221/#225). Set by the Node Host on every network.</summary>
public sealed class ServiceMetricsOptions
{
    public bool Enabled { get; set; }

    public string RpcUrl { get; set; } = "";

    public string RewardDistributorAddress { get; set; } = "";

    public string EpochControllerAddress { get; set; } = "";

    public int PollSeconds { get; set; } = 15;
}

/// <summary>The chain reads the gateway's reward plumbing needs; read-only, never signs or spends gas.</summary>
public interface IServiceMetricsChain
{
    Task<ulong> ChainIdAsync(CancellationToken ct);

    Task<ulong> CurrentEpochAsync(CancellationToken ct);

    /// <summary>The epoch's start, unix seconds.</summary>
    Task<long> EpochStartAsync(ulong epoch, CancellationToken ct);

    /// <summary><c>reward.metric.signer</c>: whose probes get the reserved lane (#225).</summary>
    Task<string> MetricSignerAsync(CancellationToken ct);
}

public sealed class NethereumServiceMetricsChain(ServiceMetricsOptions options) : IServiceMetricsChain
{
    private static readonly byte[] MetricSignerKey = Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("reward.metric.signer"));

    private readonly Web3 _web3 = new(options.RpcUrl);
    private ulong? _chainId;

    public async Task<ulong> ChainIdAsync(CancellationToken ct) => _chainId ??= (ulong)(await _web3.Eth.ChainId.SendRequestAsync()).Value;

    public async Task<ulong> CurrentEpochAsync(CancellationToken ct) => (ulong)await _web3.Eth.GetContractQueryHandler<CurrentEpochFunction>()
        .QueryAsync<BigInteger>(options.EpochControllerAddress, new CurrentEpochFunction());

    public async Task<long> EpochStartAsync(ulong epoch, CancellationToken ct) => (long)await _web3.Eth.GetContractQueryHandler<EpochStartFunction>()
        .QueryAsync<BigInteger>(options.EpochControllerAddress, new EpochStartFunction { Epoch = epoch });

    public async Task<string> MetricSignerAsync(CancellationToken ct)
    {
        var config = await _web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(options.RewardDistributorAddress, new ConfigFunction());
        return await _web3.Eth.GetContractQueryHandler<GetAddressFunction>().QueryAsync<string>(config, new GetAddressFunction { Key = MetricSignerKey });
    }
}
