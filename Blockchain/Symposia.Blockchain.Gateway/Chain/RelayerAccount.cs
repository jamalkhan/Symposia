using Microsoft.Extensions.Options;
using Nethereum.RPC.NonceServices;
using Nethereum.Web3;

namespace Symposia.Blockchain.Gateway.Chain;

/// <summary>
/// The one relayer account every Gateway write goes through: node registration, epoch roots, the
/// epoch sealer (#72) and the telemetry relays (#158). Sends are serialized through one gate over
/// one in-memory nonce tracker, so concurrent features can't collide on a nonce. After a failed
/// send the nonce is re-read from the chain so a dropped transaction can't wedge later ones.
/// Reads use <see cref="Web3"/> directly and never wait on the gate.
/// </summary>
public sealed class RelayerAccount
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Nethereum.Web3.Accounts.Account _account;

    public RelayerAccount(IOptions<GatewayOptions> options)
    {
        var o = options.Value;
        _account = new Nethereum.Web3.Accounts.Account(o.RelayerPrivateKey);
        Web3 = new Web3(_account, o.RpcUrl);
        _account.NonceService = new InMemoryNonceService(_account.Address, Web3.Client);
    }

    public Web3 Web3 { get; }

    public string Address => _account.Address;

    public async Task<T> SendAsync<T>(Func<Web3, Task<T>> send, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await send(Web3);
        }
        catch
        {
            await _account.NonceService.ResetNonceAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
