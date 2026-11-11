using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.Util;
using Symposia.Blockchain.Contracts;
using Symposia.Blockchain.Gateway.Chain;
using Symposia.Network.Node.Staking;

namespace Symposia.Blockchain.Gateway.Staking;

/// <summary>
/// LocalDev funding and verification so every host can stake its Node Services (issue #200). The
/// relayer key here is the anvil dev key: the token's dev minter and the registry's verifier. A
/// node proves it holds its key (<see cref="DevFunding"/>); the gateway then tops up its gas and SYM
/// (never past what its services' stakes need, nor past <see cref="DevFundingOptions.MaxGrantSym"/>),
/// or promotes a PendingVerification service to Active. Suspended or banned services are refused, so
/// penalties (#166) can't be undone here. Mapped only when enabled, and every call is refused unless
/// the chain is anvil, so a misconfigured public gateway can't hand out tokens.
/// </summary>
public static class DevFundingEndpoints
{
    private static readonly TimeSpan PerNodeInterval = TimeSpan.FromMinutes(1);

    public static IEndpointRouteBuilder MapDevFunding(this IEndpointRouteBuilder app)
    {
        var state = new State();
        app.MapPost(DevFunding.FundPath, async (DevFundRequest req, RelayerAccount relayer, LocalDevChain chain, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            if (!await chain.IsLocalDevAsync() || options.Value.StakingRegistryAddress.Length == 0)
            {
                return Results.NotFound();
            }

            if (req.Services is not { Count: > 0 and <= 8 } || req.Services.Any(s => !BigInteger.TryParse(s.Capacity, NumberStyles.None, CultureInfo.InvariantCulture, out var c) || c > ulong.MaxValue))
            {
                return Results.BadRequest("services must list 1-8 services with a decimal capacity.");
            }

            if (DevFunding.Validate(req.Node, req.UnixSeconds, req.Signature, DevFunding.FundDigest(DevFunding.LocalDevChainId, req.Node, req.Services, req.UnixSeconds), DateTimeOffset.UtcNow) is { } invalid)
            {
                return Refused(invalid);
            }

            if (!state.Admit("fund:" + req.Node))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var o = options.Value;
            var registry = o.StakingRegistryAddress;
            var eth = relayer.Web3.Eth;
            BigInteger needed = 0;
            foreach (var s in req.Services)
            {
                needed += await eth.GetContractQueryHandler<MinStakeForFunction>().QueryAsync<BigInteger>(registry,
                    new MinStakeForFunction { NodeType = s.Service, Capacity = BigInteger.Parse(s.Capacity, CultureInfo.InvariantCulture) });
            }

            var token = await TokenAsync(relayer, registry);
            var balance = await eth.GetContractQueryHandler<BalanceOfFunction>().QueryAsync<BigInteger>(token, new BalanceOfFunction { Account = req.Node });
            var target = BigInteger.Min(2 * needed, UnitConversion.Convert.ToWei(o.DevFunding.MaxGrantSym));
            var minted = BigInteger.Max(0, target - balance);
            if (minted > 0)
            {
                var receipt = await relayer.SendAsync(token, new MintFunction { To = req.Node, Amount = minted }, ct);
                if (receipt.Status.Value != 1)
                {
                    return Results.Problem($"Mint {receipt.TransactionHash} reverted.", statusCode: StatusCodes.Status502BadGateway);
                }
            }

            var gas = UnitConversion.Convert.FromWei((await eth.GetBalance.SendRequestAsync(req.Node)).Value);
            var sent = gas < o.DevFunding.GasEther / 2 ? o.DevFunding.GasEther - gas : 0m;
            if (sent > 0)
            {
                await relayer.SendEtherAsync(req.Node, sent, ct);
            }

            return Results.Ok(new { mintedWei = minted.ToString(CultureInfo.InvariantCulture), gasSentEther = sent });
        });

        app.MapPost(DevFunding.VerifyPath, async (DevVerifyRequest req, RelayerAccount relayer, LocalDevChain chain, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            var o = options.Value;
            if (!await chain.IsLocalDevAsync() || o.StakingRegistryAddress.Length == 0 || o.EpochControllerAddress.Length == 0)
            {
                return Results.NotFound();
            }

            if (DevFunding.Validate(req.Node, req.UnixSeconds, req.Signature, DevFunding.VerifyDigest(DevFunding.LocalDevChainId, req.Node, req.Service, req.UnixSeconds), DateTimeOffset.UtcNow) is { } invalid)
            {
                return Refused(invalid);
            }

            var status = (ServiceStatus)await relayer.Web3.Eth.GetContractQueryHandler<ServiceStatusOfFunction>()
                .QueryAsync<byte>(o.StakingRegistryAddress, new ServiceStatusOfFunction { Node = req.Node, Service = req.Service });
            if (status == ServiceStatus.Active)
            {
                return Results.Ok(new { status = status.ToString() });
            }

            if (status != ServiceStatus.PendingVerification)
            {
                return Results.Conflict(new { status = status.ToString(), error = "only a PendingVerification service can be verified" });
            }

            if (!state.Admit($"verify:{req.Node}:{req.Service}"))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var epoch = await relayer.Web3.Eth.GetContractQueryHandler<CurrentEpochFunction>().QueryAsync<BigInteger>(o.EpochControllerAddress, new CurrentEpochFunction());
            var receipt = await relayer.SendAsync(o.StakingRegistryAddress,
                new RecordServiceVerificationFunction { Node = req.Node, Service = req.Service, Passed = true, Epoch = epoch }, ct);
            return receipt.Status.Value == 1
                ? Results.Ok(new { status = ServiceStatus.Active.ToString(), epoch = (ulong)epoch })
                : Results.Problem($"Verification {receipt.TransactionHash} reverted.", statusCode: StatusCodes.Status502BadGateway);
        });

        return app;
    }

    /// <summary>401 for a missing, stale or malformed signature; 403 for a valid one by someone other than the node.</summary>
    private static IResult Refused(string reason) =>
        Results.Json(new { error = reason }, statusCode: reason == DevFunding.WrongSigner ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized);

    private static async Task<string> TokenAsync(RelayerAccount relayer, string registry)
    {
        var config = await relayer.Web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(registry, new ConfigFunction());
        return await relayer.Web3.Eth.GetContractQueryHandler<GetAddressFunction>().QueryAsync<string>(config, new GetAddressFunction { Key = ConfigKey.TokenAddress });
    }

    private sealed class State
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new();
        public bool Admit(string key)
        {
            var now = DateTimeOffset.UtcNow;
            if (_last.Count > 10_000)
            {
                foreach (var (k, at) in _last)
                {
                    if (now - at > PerNodeInterval) _last.TryRemove(k, out _);
                }
            }

            var previous = _last.GetOrAdd(key, DateTimeOffset.MinValue);
            return now - previous >= PerNodeInterval && _last.TryUpdate(key, now, previous);
        }
    }
}
