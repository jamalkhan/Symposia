using Microsoft.Extensions.Options;
using Nethereum.RPC.NonceServices;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace Symposia.Blockchain.Gateway.Chain;

/// <summary>
/// The one relayer account every Gateway write goes through: node registration, epoch roots, the
/// epoch sealer (#72) and the telemetry relays (#158). Nonce assignment and broadcast are
/// serialized through one gate over one in-memory nonce tracker, so concurrent features can't
/// collide on a nonce; the wait for a receipt happens outside the gate (#163), so writes broadcast
/// back to back and mine together instead of queueing a block apart. At most
/// <see cref="MaxInFlight"/> transactions wait at once, so a stuck nonce can't pile up hundreds.
/// After a failed broadcast the nonce is re-read from the chain so a dropped transaction can't wedge
/// later ones. Reads use <see cref="Web3"/> directly and never wait on the gate.
/// </summary>
public sealed class RelayerAccount
{
    public const int MaxInFlight = 16;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _inFlight = new(MaxInFlight, MaxInFlight);
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

    /// <summary>Sends <paramref name="function"/> to <paramref name="contract"/> and waits for its receipt (status unchecked).</summary>
    public async Task<TransactionReceipt> SendAsync<TFunction>(string contract, TFunction function, CancellationToken ct = default)
        where TFunction : FunctionMessage, new()
    {
        await _inFlight.WaitAsync(ct);
        try
        {
            string hash;
            await _gate.WaitAsync(ct);
            try
            {
                hash = await Web3.Eth.GetContractTransactionHandler<TFunction>().SendRequestAsync(contract, function);
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

            return await Web3.TransactionReceiptPolling.PollForReceiptAsync(hash, ct);
        }
        finally
        {
            _inFlight.Release();
        }
    }
}
