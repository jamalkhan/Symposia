using System.Numerics;
using Microsoft.Extensions.Logging;
using Nethereum.Web3;
using Symposia.Blockchain.Contracts;

namespace Symposia.Network.Node.Staking;

public enum StakeOutcome
{
    /// <summary>The service is Active: staked and verified.</summary>
    Active,

    /// <summary>Not there yet (funding throttled, verification pending); try again later.</summary>
    Retry,

    /// <summary>The network has no stake funding (an older LocalDev host, or not LocalDev): don't retry.</summary>
    Unavailable,

    /// <summary>Can't be staked as things stand: Suspended, Banned or Deregistering, or funding falls short of the stake.</summary>
    Blocked,
}

/// <summary>
/// Stakes a node's Node Services (issue #200): funding from an <see cref="IStakeFunding"/> source, then
/// <c>approve</c> and <c>registerService</c> with the node's own key, then verification. One instance
/// per node, and one service at a time: <c>approve</c> sets (not adds to) the allowance and every
/// transaction shares the node's nonce, so concurrent stakers on one key would trip over each other.
/// Each pass reads the registry first and does only what's missing, so restarts never double-stake.
/// </summary>
public sealed class NodeServiceStaker(NodeIdentity identity, ulong chainId, string rpcUrl, string stakingRegistry, IStakeFunding funding,
    ILogger logger, HttpClient? rpcHttp = null)
{
    /// <summary>The single region LocalDev nodes claim.</summary>
    public static readonly byte[] LocalDevRegion = Region("localdev");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Web3? _web3;

    /// <summary>Stakes each of <paramref name="services"/> that isn't Active yet; returns each one's outcome.</summary>
    public async Task<IReadOnlyDictionary<byte, StakeOutcome>> EnsureStakedAsync(
        IReadOnlyList<(byte Service, BigInteger Capacity)> services, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var web3 = _web3 ??= rpcHttp is null // one client: Nethereum sets BaseAddress on the HttpClient it's given
                ? new Web3(identity.ChainAccount(chainId), rpcUrl)
                : new Web3(identity.ChainAccount(chainId), new Nethereum.JsonRpc.Client.RpcClient(new Uri(rpcUrl), rpcHttp));
            var node = identity.NodeId;
            var outcomes = new Dictionary<byte, StakeOutcome>();
            var statuses = new Dictionary<byte, ServiceStatus>();
            foreach (var (service, _) in services)
            {
                statuses[service] = await StatusAsync(web3, node, service);
            }

            // Fund once for every service still to register, so the grant covers them together.
            var unregistered = services.Where(s => statuses[s.Service] == ServiceStatus.Unregistered).ToList();
            if (unregistered.Count > 0)
            {
                var funded = await funding.FundAsync(unregistered, ct);
                if (funded != FundingResponse.Ok)
                {
                    foreach (var (service, _) in unregistered)
                    {
                        outcomes[service] = Map(funded);
                    }
                }
            }

            foreach (var (service, capacity) in services)
            {
                if (outcomes.ContainsKey(service))
                {
                    continue;
                }

                outcomes[service] = await StakeOneAsync(web3, node, service, capacity, statuses[service], ct);
            }

            return outcomes;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StakeOutcome> StakeOneAsync(Web3 web3, string node, byte service, BigInteger capacity, ServiceStatus status, CancellationToken ct)
    {
        switch (status)
        {
            case ServiceStatus.Active:
                return StakeOutcome.Active;
            case ServiceStatus.Suspended or ServiceStatus.Banned or ServiceStatus.Deregistering:
                return StakeOutcome.Blocked;
            case ServiceStatus.Unregistered:
                var stake = await web3.Eth.GetContractQueryHandler<MinStakeForFunction>()
                    .QueryAsync<BigInteger>(stakingRegistry, new MinStakeForFunction { NodeType = service, Capacity = capacity });
                var token = await TokenAsync(web3);
                var held = await web3.Eth.GetContractQueryHandler<BalanceOfFunction>().QueryAsync<BigInteger>(token, new BalanceOfFunction { Account = node });
                if (held < stake)
                {
                    // Funding is capped below this stake: registering would revert every retry and burn gas.
                    logger.LogWarning("Funding covers {Held} wei but service {Service} needs {Stake} wei; not staking it.", held, service, stake);
                    return StakeOutcome.Blocked;
                }

                await SendAsync(web3, token, new ApproveFunction { Spender = stakingRegistry, Value = stake }, "approve", ct);
                await SendAsync(web3, stakingRegistry,
                    new RegisterServiceFunction { Service = service, Capacity = capacity, Region = LocalDevRegion, StakeAmount = stake }, "registerService", ct);
                logger.LogInformation("Staked {Stake} wei for service {Service}; awaiting verification.", stake, service);
                break;
        }

        var verified = await funding.VerifyAsync(service, ct);
        if (verified != FundingResponse.Ok)
        {
            return Map(verified);
        }

        return await StatusAsync(web3, node, service) == ServiceStatus.Active ? StakeOutcome.Active : StakeOutcome.Retry;
    }

    private static StakeOutcome Map(FundingResponse response) => response switch
    {
        FundingResponse.Unavailable => StakeOutcome.Unavailable,
        FundingResponse.Conflict => StakeOutcome.Blocked,
        _ => StakeOutcome.Retry,
    };

    private async Task<ServiceStatus> StatusAsync(Web3 web3, string node, byte service) =>
        (ServiceStatus)await web3.Eth.GetContractQueryHandler<ServiceStatusOfFunction>()
            .QueryAsync<byte>(stakingRegistry, new ServiceStatusOfFunction { Node = node, Service = service });

    private async Task<string> TokenAsync(Web3 web3)
    {
        var config = await web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(stakingRegistry, new ConfigFunction());
        return await web3.Eth.GetContractQueryHandler<GetAddressFunction>().QueryAsync<string>(config, new GetAddressFunction { Key = ConfigKey.TokenAddress });
    }

    private static async Task SendAsync<TFunction>(Web3 web3, string contract, TFunction function, string name, CancellationToken ct)
        where TFunction : Nethereum.Contracts.FunctionMessage, new()
    {
        Nethereum.RPC.Eth.DTOs.TransactionReceipt receipt;
        try
        {
            receipt = await web3.Eth.GetContractTransactionHandler<TFunction>().SendRequestAndWaitForReceiptAsync(contract, function, ct);
        }
        catch (Nethereum.Contracts.SmartContractCustomErrorRevertException ex)
        {
            throw new InvalidOperationException($"{name} reverted: {ex.Message} {ex.ExceptionEncodedData}", ex);
        }

        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException($"{name} transaction {receipt.TransactionHash} reverted.");
        }
    }

    private static byte[] Region(string name)
    {
        var bytes = new byte[32];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        return bytes;
    }
}
