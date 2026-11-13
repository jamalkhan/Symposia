using Microsoft.Extensions.Options;
using Symposia.Blockchain.Contracts;
using Symposia.Blockchain.Gateway.Chain;

namespace Symposia.Blockchain.Gateway.Rewards;

/// <summary>
/// The network's VerifierAssignments contract (#227), read from the distributor's config, which is the one source of
/// truth. A found address is cached for <see cref="Ttl"/> (governance can re-point it); "none" is never cached, so a
/// network that configures it later is picked up on the next call.
/// </summary>
public sealed class VerifierAssignmentsResolver(RelayerAccount relayer, IOptions<GatewayOptions> options, TimeProvider? time = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private (string Address, ulong ChainId, DateTimeOffset At)? _cached;

    /// <summary>The contract and chain ID, or null when the network has no VerifierAssignments.</summary>
    public async Task<(string Address, ulong ChainId)?> ResolveAsync()
    {
        if (_cached is { } c && _time.GetUtcNow() - c.At < Ttl)
        {
            return (c.Address, c.ChainId);
        }

        var distributor = options.Value.RewardDistributorAddress;
        if (string.IsNullOrEmpty(distributor))
        {
            return null;
        }

        var config = await relayer.Web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(distributor, new ConfigFunction());
        var address = await relayer.Web3.Eth.GetContractQueryHandler<GetAddressFunction>().QueryAsync<string>(config, new GetAddressFunction { Key = ConfigKeys.VerifierAssignments });
        if (string.IsNullOrEmpty(address) || address == "0x0000000000000000000000000000000000000000")
        {
            return null;
        }

        var chainId = (ulong)(await relayer.Web3.Eth.ChainId.SendRequestAsync()).Value;
        _cached = (address, chainId, _time.GetUtcNow());
        return (address, chainId);
    }
}
