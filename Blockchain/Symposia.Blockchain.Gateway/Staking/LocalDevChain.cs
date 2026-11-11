using Microsoft.Extensions.Options;
using Symposia.Blockchain.Gateway.Chain;
using Symposia.Network.Node.Staking;

namespace Symposia.Blockchain.Gateway.Staking;

/// <summary>
/// The double guard on everything dev-only (#200): <see cref="DevFundingOptions.Enabled"/>, which only
/// the Node Host's LocalDev gateway sets, and the chain id read from the RPC (cached), never trusted
/// from config. Shared by the dev endpoints and the RPC proxy's LocalDev write methods.
/// </summary>
public sealed class LocalDevChain(IServiceProvider services, IOptions<GatewayOptions> options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _isAnvil;

    public async Task<bool> IsLocalDevAsync()
    {
        if (!options.Value.DevFunding.Enabled)
        {
            return false;
        }

        if (_isAnvil is { } known)
        {
            return known;
        }

        await _gate.WaitAsync();
        try
        {
            // Resolved only here: a gateway without a relayer key (most aren't LocalDev) never builds one for this.
            var relayer = services.GetRequiredService<RelayerAccount>();
            _isAnvil ??= (ulong)(await relayer.Web3.Eth.ChainId.SendRequestAsync()).Value == DevFunding.LocalDevChainId;
            return _isAnvil.Value;
        }
        finally
        {
            _gate.Release();
        }
    }
}
