using Microsoft.Extensions.Options;
using Nethereum.Web3;

namespace Symposia.Blockchain.Gateway.Chain;

/// <summary>
/// Relays signed registration and epoch-root payloads to the deployed
/// NodeRegistry/EpochRootRegistry contracts on the foundation-operated L3
/// sequencer, paying gas from the relayer account (see the Arch pass on
/// issue #110, "Gas bootstrapping"). Reads go straight to the contracts'
/// view functions, which cost no gas and need no relaying.
/// </summary>
public sealed class BootstrapChainClient
{
    private readonly Web3 _web3;
    private readonly string _nodeRegistryAddress;
    private readonly string _epochRootRegistryAddress;

    public BootstrapChainClient(IOptions<GatewayOptions> options)
    {
        var o = options.Value;
        _web3 = new Web3(new Nethereum.Web3.Accounts.Account(o.RelayerPrivateKey), o.RpcUrl);
        _nodeRegistryAddress = o.NodeRegistryAddress;
        _epochRootRegistryAddress = o.EpochRootRegistryAddress;
    }

    public Task<bool> IsRegisteredAsync(string node) =>
        _web3.Eth.GetContractQueryHandler<IsRegisteredFunction>()
            .QueryAsync<bool>(_nodeRegistryAddress, new IsRegisteredFunction { Node = node });

    /// <summary>
    /// Relays a registration transaction. The contract itself is idempotent
    /// (Functional Requirement 10), so this always submits and lets the
    /// contract short-circuit on repeats; callers that want to avoid the gas
    /// cost of a known-redundant relay should check <see cref="IsRegisteredAsync"/>
    /// first.
    /// </summary>
    public Task<string> RegisterAsync(string node, byte[] signature) =>
        _web3.Eth.GetContractTransactionHandler<RegisterFunction>()
            .SendRequestAndWaitForReceiptAsync(_nodeRegistryAddress, new RegisterFunction
            {
                Node = node,
                Signature = signature,
            }).ContinueWith(t => t.Result.TransactionHash);

    public Task<string> SubmitRootAsync(string node, ulong epoch, byte[] root, byte[] signature) =>
        _web3.Eth.GetContractTransactionHandler<SubmitRootFunction>()
            .SendRequestAndWaitForReceiptAsync(_epochRootRegistryAddress, new SubmitRootFunction
            {
                Node = node,
                Epoch = epoch,
                Root = root,
                Signature = signature,
            }).ContinueWith(t => t.Result.TransactionHash);

    public async Task<(ulong Epoch, byte[] Root)?> TryGetLatestRootAsync(string node)
    {
        // Registered-but-no-submissions is a well-defined, distinct state
        // from never-registered (open question raised in the QA plan,
        // resolved by the contract reverting only for "no submissions").
        try
        {
            var result = await _web3.Eth.GetContractQueryHandler<GetLatestRootFunction>()
                .QueryDeserializingToObjectAsync<GetLatestRootOutputDTO>(
                    new GetLatestRootFunction { Node = node }, _epochRootRegistryAddress);
            return (result.Epoch, result.Root);
        }
        catch (Exception)
        {
            // The view function reverts for "no submissions yet" (by design,
            // so callers can distinguish that from a genuine all-zero root)
            // — any failure calling it here means "nothing to return".
            return null;
        }
    }

    public Task<byte[]> GetRootAsync(string node, ulong epoch) =>
        _web3.Eth.GetContractQueryHandler<GetRootFunction>()
            .QueryAsync<byte[]>(_epochRootRegistryAddress, new GetRootFunction { Node = node, Epoch = epoch });
}
