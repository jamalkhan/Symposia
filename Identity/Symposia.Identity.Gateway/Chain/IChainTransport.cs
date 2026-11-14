using Nethereum.Contracts;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.RPC.NonceServices;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Symposia.Blockchain.Contracts;

namespace Symposia.Identity.Gateway.Chain;

/// <summary>
/// The only code that touches the node for writes (#222): sign, broadcast, and look up. A seam so tests can script a
/// timeout after broadcast without a chain.
/// </summary>
internal interface IChainTransport
{
    /// <summary>Estimates (reverting here means the chain refuses it), adds <see cref="GasHeadroom"/>, and signs with the next nonce.</summary>
    Task<string> SignAsync<TFunction>(string contract, TFunction function, CancellationToken ct)
        where TFunction : FunctionMessage, new();

    Task SendRawAsync(string signedTx, CancellationToken ct);

    Task<TransactionReceipt?> GetReceiptAsync(string txHash, CancellationToken ct);

    Task<Transaction?> GetTransactionAsync(string txHash, CancellationToken ct);

    /// <summary>Re-reads the nonce from the chain, after a transaction that may not have reached it.</summary>
    Task ResetNonceAsync();
}

internal sealed class NethereumChainTransport : IChainTransport
{
    private readonly Account _account;

    public NethereumChainTransport(string privateKey, string rpcUrl)
    {
        _account = new Account(privateKey);
        Web3 = new Web3(_account, rpcUrl);
        _account.NonceService = new InMemoryNonceService(_account.Address, Web3.Client);
    }

    public Web3 Web3 { get; }

    public Task<string> SignAsync<TFunction>(string contract, TFunction function, CancellationToken ct)
        where TFunction : FunctionMessage, new()
    {
        var handler = Web3.Eth.GetContractTransactionHandler<TFunction>();
        return handler.WithHeadroomAsync(contract, function, () => handler.SignTransactionAsync(contract, function)).WaitAsync(ct);
    }

    /// <summary><paramref name="ct"/> only stops the wait: the request may still reach the node, so a cancelled send's outcome is unknown.</summary>
    public Task SendRawAsync(string signedTx, CancellationToken ct) =>
        Web3.Eth.Transactions.SendRawTransaction.SendRequestAsync(signedTx.EnsureHexPrefix()).WaitAsync(ct);

    public async Task<TransactionReceipt?> GetReceiptAsync(string txHash, CancellationToken ct) =>
        await Web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash).WaitAsync(ct);

    public async Task<Transaction?> GetTransactionAsync(string txHash, CancellationToken ct) =>
        await Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(txHash).WaitAsync(ct);

    public Task ResetNonceAsync() => _account.NonceService.ResetNonceAsync();
}
