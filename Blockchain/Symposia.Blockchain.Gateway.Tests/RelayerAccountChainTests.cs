using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Symposia.Blockchain.Contracts;
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

    [Theory]
    [InlineData(0, 30_000)]
    [InlineData(21_000, 55_200)]
    [InlineData(64_322, 107_186)]
    [InlineData(10_000_000, 12_030_000)]
    public void WithHeadroom_AddsTwentyPercentAndOneFreshSlot(long estimate, long limit) =>
        Assert.Equal(new BigInteger(limit), GasHeadroom.Over(estimate));

    [Fact]
    public async Task Send_CarriesHeadroomOverTheEstimate()
    {
        var relayer = Relayer();
        var token = chain.Addresses["SYMToken"];
        var mint = new MintFunction { To = relayer.Address, Amount = 1 };
        var estimate = await relayer.Web3.Eth.GetContractTransactionHandler<MintFunction>()
            .EstimateGasAsync(token, new MintFunction { To = relayer.Address, Amount = 1 });

        var receipt = await relayer.SendAsync(token, mint);

        Assert.Equal(1, (int)receipt.Status.Value);
        var tx = await relayer.Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(receipt.TransactionHash);
        Assert.True(tx.Gas.Value >= GasHeadroom.Over(estimate.Value), $"gas {tx.Gas.Value} under headroom over {estimate.Value}");
        Assert.Null(mint.Gas); // set for that send only (#216)
    }

    [Fact]
    public async Task Send_KeepsACallerSetGasLimit()
    {
        var relayer = Relayer();
        var receipt = await relayer.SendAsync(chain.Addresses["SYMToken"], new MintFunction { To = relayer.Address, Amount = 1, Gas = 500_000 });

        var tx = await relayer.Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(receipt.TransactionHash);
        Assert.Equal(new BigInteger(500_000), tx.Gas.Value);
    }

    [Fact]
    public async Task A_send_that_reverts_in_estimation_throws_and_the_next_send_still_mines()
    {
        var relayer = Relayer();
        var token = chain.Addresses["SYMToken"];

        var before = await relayer.Web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(relayer.Address, BlockParameter.CreatePending());

        // Amount 0 reverts with ZeroAmount, so the estimate fails before anything is signed.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => relayer.SendAsync(token, new MintFunction { To = relayer.Address, Amount = 0 }));
        Assert.DoesNotContain("intrinsic gas", ex.Message);
        var receipt = await relayer.SendAsync(token, new MintFunction { To = relayer.Address, Amount = 1 });

        Assert.Equal(1, (int)receipt.Status.Value);
        var tx = await relayer.Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(receipt.TransactionHash);
        Assert.Equal(before.Value, tx.Nonce.Value); // the failed estimate consumed no nonce
    }

    private RelayerAccount Relayer()
    {
        var relayer = new RelayerAccount(Options.Create(new GatewayOptions { RpcUrl = chain.RpcUrl, RelayerPrivateKey = RewardsChainFixture.DeployerKey }));
        relayer.Web3.TransactionManager.UseLegacyAsDefault = true;
        return relayer;
    }
}
