using System.Numerics;
using Nethereum.Signer;
using Nethereum.Web3;
using Symposia.Blockchain.Gateway.Tests;
using Symposia.NodeHost.Ledger;

namespace Symposia.NodeHost.Tests;

/// <summary>The ledger reads real registry events from an anvil chain deployed by DeployRewards (issue #161; QA LED-10).</summary>
public sealed class LedgerChainTests(RewardsChainFixture chain) : IClassFixture<RewardsChainFixture>
{
    private const string Abi = """
    [
      {"type":"function","name":"mint","inputs":[{"name":"to","type":"address"},{"name":"amount","type":"uint256"}],"outputs":[],"stateMutability":"nonpayable"},
      {"type":"function","name":"approve","inputs":[{"name":"s","type":"address"},{"name":"a","type":"uint256"}],"outputs":[{"type":"bool"}],"stateMutability":"nonpayable"},
      {"type":"function","name":"registerService","inputs":[{"name":"s","type":"uint8"},{"name":"c","type":"uint256"},{"name":"r","type":"bytes32"},{"name":"a","type":"uint256"}],"outputs":[],"stateMutability":"nonpayable"},
      {"type":"function","name":"topUpService","inputs":[{"name":"n","type":"address"},{"name":"s","type":"uint8"},{"name":"a","type":"uint256"}],"outputs":[],"stateMutability":"nonpayable"}
    ]
    """;

    [Fact]
    public async Task RegistrationsAndTopUps_AppearPerService()
    {
        var a = chain.Addresses;
        var deployer = new Web3(new Nethereum.Web3.Accounts.Account(RewardsChainFixture.DeployerKey), chain.RpcUrl);
        var nodeWeb3 = new Web3(new Nethereum.Web3.Accounts.Account(RewardsChainFixture.NodeKey), chain.RpcUrl);
        deployer.TransactionManager.UseLegacyAsDefault = nodeWeb3.TransactionManager.UseLegacyAsDefault = true;
        var node = new EthECKey(RewardsChainFixture.NodeKey).GetPublicAddress();
        var sym = BigInteger.Pow(10, 18);

        await Send(deployer, a["SYMToken"], "mint", node, 1_000 * sym);
        await Send(nodeWeb3, a["SYMToken"], "approve", a["StakingNodeRegistry"], 1_000 * sym);
        await Send(nodeWeb3, a["StakingNodeRegistry"], "registerService", (byte)0, BigInteger.Zero, new byte[32], 100 * sym);
        await Send(nodeWeb3, a["StakingNodeRegistry"], "registerService", (byte)8, BigInteger.Zero, new byte[32], 40 * sym);

        var source = new NethereumLedgerLogSource(chain.RpcUrl, a["StakingNodeRegistry"], a["RewardDistributor"]);
        var ledger = new LedgerService(() => source, node, () => null);
        var first = await ledger.SnapshotAsync(default);

        Assert.True(first.Available);
        Assert.Equal(
            [("Storage", "Service registered", "-100"), ("Blob Gateway", "Service registered", "-40")],
            first.Entries.Select(e => (e.Service, e.Type, e.Amount)));

        await Send(nodeWeb3, a["StakingNodeRegistry"], "topUpService", node, (byte)8, 5 * sym);
        var second = await ledger.SnapshotAsync(default);

        Assert.Equal(("Blob Gateway", "Top-up", "-5"), (second.Entries[^1].Service, second.Entries[^1].Type, second.Entries[^1].Amount));
        Assert.Equal("45", second.Totals.Single(t => t.Service == "Blob Gateway").Stake);
        Assert.Equal("0", second.Totals.Single(t => t.Service == "Storage").ReserveHeld);
        Assert.True(second.ScannedToBlock > first.ScannedToBlock);
    }

    private static async Task Send(Web3 web3, string contract, string function, params object[] args)
    {
        var receipt = await web3.Eth.GetContract(Abi, contract).GetFunction(function)
            .SendTransactionAndWaitForReceiptAsync(web3.TransactionManager.Account.Address, new Nethereum.Hex.HexTypes.HexBigInteger(3_000_000), null, null, args);
        Assert.Equal(1, (int)receipt.Status.Value);
    }
}
