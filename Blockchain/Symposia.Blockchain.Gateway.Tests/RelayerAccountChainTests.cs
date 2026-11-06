using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Symposia.Blockchain.Gateway.Chain;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>#163: concurrent writes broadcast back to back with distinct nonces and all mine.</summary>
public sealed class RelayerAccountChainTests(RewardsChainFixture chain) : IClassFixture<RewardsChainFixture>
{
    [Function("mint")]
    private sealed class MintFunction : FunctionMessage
    {
        [Parameter("address", "to", 1)] public string To { get; set; } = "";
        [Parameter("uint256", "amount", 2)] public BigInteger Amount { get; set; }
    }

    [Fact]
    public async Task ConcurrentSends_GetDistinctNoncesAndAllSucceed()
    {
        var relayer = new RelayerAccount(Options.Create(new GatewayOptions { RpcUrl = chain.RpcUrl, RelayerPrivateKey = RewardsChainFixture.DeployerKey }));
        relayer.Web3.TransactionManager.UseLegacyAsDefault = true;
        var token = chain.Addresses["SYMToken"];

        var receipts = await Task.WhenAll(Enumerable.Range(1, 8).Select(i =>
            relayer.SendAsync(token, new MintFunction { To = relayer.Address, Amount = i })));

        Assert.All(receipts, r => Assert.Equal(1, (int)r.Status.Value));
        var nonces = await Task.WhenAll(receipts.Select(async r =>
            (await relayer.Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(r.TransactionHash)).Nonce.Value));
        Assert.Equal(8, nonces.Distinct().Count());
    }
}
