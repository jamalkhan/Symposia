using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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

    /// <summary>The network has no dev funding (an older LocalDev host, or not LocalDev): don't retry.</summary>
    Unavailable,

    /// <summary>The service is Suspended, Banned or Deregistering; staking can't change that.</summary>
    Blocked,
}

/// <summary>
/// Stakes one of this node's Node Services on a LocalDev network (issue #200), so it can earn rewards
/// locally: dev funds from the LocalDev gateway, then <c>approve</c> and <c>registerService</c> with the
/// node's own key, then a verification from the gateway. Each call reads the registry first and does
/// only what's missing, so restarts and repeats never double-stake.
/// </summary>
public sealed class NodeServiceStaker(
    NodeIdentity identity, ulong chainId, string rpcUrl, Uri gateway, string stakingRegistry, HttpClient http, ILogger logger,
    HttpClient? rpcHttp = null)
{
    /// <summary>The single region LocalDev nodes claim.</summary>
    public static readonly byte[] LocalDevRegion = Region("localdev");

    private Web3? _web3;

    public async Task<StakeOutcome> EnsureStakedAsync(byte service, BigInteger capacity, CancellationToken ct)
    {
        var web3 = _web3 ??= rpcHttp is null // one client: Nethereum sets BaseAddress on the HttpClient it's given
            ? new Web3(identity.ChainAccount(chainId), rpcUrl)
            : new Web3(identity.ChainAccount(chainId), new Nethereum.JsonRpc.Client.RpcClient(new Uri(rpcUrl), rpcHttp));
        var node = identity.NodeId;
        var status = await StatusAsync(web3, node, service);
        if (status is ServiceStatus.Active)
        {
            return StakeOutcome.Active;
        }

        if (status is ServiceStatus.Suspended or ServiceStatus.Banned or ServiceStatus.Deregistering)
        {
            return StakeOutcome.Blocked;
        }

        if (status is ServiceStatus.Unregistered)
        {
            var services = new[] { new DevFundService(service, capacity.ToString(CultureInfo.InvariantCulture)) };
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fund = await PostAsync(DevFunding.FundPath,
                new DevFundRequest(node, services, ts, identity.SignDevFunding(DevFunding.FundDigest(chainId, node, services, ts))), ct);
            if (fund != StakeOutcome.Active)
            {
                return fund;
            }

            var stake = await web3.Eth.GetContractQueryHandler<MinStakeForFunction>()
                .QueryAsync<BigInteger>(stakingRegistry, new MinStakeForFunction { NodeType = service, Capacity = capacity });
            var token = await TokenAsync(web3);
            await SendAsync(web3, token, new ApproveFunction { Spender = stakingRegistry, Value = stake }, "approve", ct);
            await SendAsync(web3, stakingRegistry,
                new RegisterServiceFunction { Service = service, Capacity = capacity, Region = LocalDevRegion, StakeAmount = stake }, "registerService", ct);
            logger.LogInformation("Staked {Stake} wei for service {Service}; awaiting verification.", stake, service);
        }

        var vts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var verified = await PostAsync(DevFunding.VerifyPath,
            new DevVerifyRequest(node, service, vts, identity.SignDevFunding(DevFunding.VerifyDigest(chainId, node, service, vts))), ct);
        return verified == StakeOutcome.Active && await StatusAsync(web3, node, service) == ServiceStatus.Active ? StakeOutcome.Active : verified switch
        {
            StakeOutcome.Active => StakeOutcome.Retry,
            _ => verified,
        };
    }

    private async Task<StakeOutcome> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(new Uri(gateway, path), body, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => StakeOutcome.Active,
            HttpStatusCode.NotFound => StakeOutcome.Unavailable,
            HttpStatusCode.Conflict => StakeOutcome.Blocked,
            _ => StakeOutcome.Retry,
        };
    }

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
