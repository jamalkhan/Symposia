using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;
using Nethereum.Web3;
using Symposia.Blockchain.Gateway.Sealing;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// End to end on a real chain (issue #72): a staked node reports for epoch 0, time passes the
/// finality window, and one sealer pass settles epochs 0 and 1 and pays the node from emission.
/// </summary>
public sealed class EpochSealerChainTests(RewardsChainFixture chain) : IClassFixture<RewardsChainFixture>
{
    private const string Abi = """
    [
      {"type":"function","name":"mint","inputs":[{"name":"to","type":"address"},{"name":"amount","type":"uint256"}],"outputs":[],"stateMutability":"nonpayable"},
      {"type":"function","name":"approve","inputs":[{"name":"s","type":"address"},{"name":"a","type":"uint256"}],"outputs":[{"type":"bool"}],"stateMutability":"nonpayable"},
      {"type":"function","name":"balanceOf","inputs":[{"name":"a","type":"address"}],"outputs":[{"type":"uint256"}],"stateMutability":"view"},
      {"type":"function","name":"minStakeFor","inputs":[{"name":"t","type":"uint8"},{"name":"c","type":"uint256"}],"outputs":[{"type":"uint256"}],"stateMutability":"view"},
      {"type":"function","name":"register","inputs":[{"name":"t","type":"uint8"},{"name":"c","type":"uint256"},{"name":"r","type":"bytes32"},{"name":"s","type":"uint256"}],"outputs":[],"stateMutability":"nonpayable"},
      {"type":"function","name":"recordVerification","inputs":[{"name":"n","type":"address"},{"name":"p","type":"bool"},{"name":"e","type":"uint256"}],"outputs":[],"stateMutability":"nonpayable"},
      {"type":"function","name":"submitMetricReport","inputs":[{"name":"n","type":"address"},{"name":"e","type":"uint256"},{"name":"s","type":"uint256"},{"name":"h","type":"uint256"},{"name":"sig","type":"bytes"}],"outputs":[],"stateMutability":"nonpayable"},
      {"type":"function","name":"nextSealableEpoch","inputs":[],"outputs":[{"type":"uint256"}],"stateMutability":"view"},
      {"type":"function","name":"finalityDeadline","inputs":[{"name":"e","type":"uint256"}],"outputs":[{"type":"uint256"}],"stateMutability":"view"}
    ]
    """;

    [Fact]
    public async Task SealerSettlesFinalEpochsAndPaysReporters()
    {
        var a = chain.Addresses;
        var deployer = new Web3(new Nethereum.Web3.Accounts.Account(RewardsChainFixture.DeployerKey), chain.RpcUrl);
        var nodeAccount = new Nethereum.Web3.Accounts.Account(RewardsChainFixture.NodeKey);
        var nodeWeb3 = new Web3(nodeAccount, chain.RpcUrl);
        var node = nodeAccount.Address;
        deployer.TransactionManager.UseLegacyAsDefault = nodeWeb3.TransactionManager.UseLegacyAsDefault = true;

        // Stake dev-minted SYM and get verified (the deployer holds the verifier and metric-signer roles).
        var stake = await Call<BigInteger>(deployer, a["StakingNodeRegistry"], "minStakeFor", (byte)0, BigInteger.One);
        await Send(deployer, a["SYMToken"], "mint", node, stake);
        await Send(nodeWeb3, a["SYMToken"], "approve", a["StakingNodeRegistry"], stake);
        await Send(nodeWeb3, a["StakingNodeRegistry"], "register", (byte)0, BigInteger.One, new byte[32], stake);
        await Send(deployer, a["StakingNodeRegistry"], "recordVerification", node, true, BigInteger.Zero);
        var balanceBefore = await Call<BigInteger>(deployer, a["SYMToken"], "balanceOf", node);

        await Send(deployer, a["RewardDistributor"], "submitMetricReport", node, BigInteger.Zero, new BigInteger(100), new BigInteger(10_000),
            SignMetric(a["RewardDistributor"], node, 0, 100, 10_000));

        var deadline = await Call<BigInteger>(deployer, a["EpochController"], "finalityDeadline", BigInteger.One);
        await SetNextBlockTimestamp((ulong)deadline + 1);

        var sealer = new EpochSealer(
            new EpochSealChain(Options.Create(Gateway()), new Symposia.Blockchain.Gateway.Chain.RelayerAccount(Options.Create(Gateway()))),
            Options.Create(Gateway()),
            NullLogger<EpochSealer>.Instance);
        Assert.Equal(2, await sealer.RunOnceAsync(default));

        Assert.Equal(new BigInteger(2), await Call<BigInteger>(deployer, a["RewardDistributor"], "nextSealableEpoch"));
        Assert.True(await Call<BigInteger>(deployer, a["SYMToken"], "balanceOf", node) > balanceBefore);
        Assert.Equal(0, await sealer.RunOnceAsync(default)); // epoch 2 is still open
    }

    private GatewayOptions Gateway() => new()
    {
        RpcUrl = chain.RpcUrl,
        RelayerPrivateKey = RewardsChainFixture.DeployerKey,
        RewardDistributorAddress = chain.Addresses["RewardDistributor"],
        EpochControllerAddress = chain.Addresses["EpochController"],
    };

    private static byte[] SignMetric(string distributor, string node, ulong epoch, ulong score, ulong heartbeatBps)
    {
        var encoded = new ABIEncode().GetABIEncoded(
            new ABIValue("address", distributor), new ABIValue("address", node), new ABIValue("uint256", epoch),
            new ABIValue("uint256", score), new ABIValue("uint256", heartbeatBps));
        var signature = new EthereumMessageSigner().Sign(Sha3Keccack.Current.CalculateHash(encoded), new EthECKey(RewardsChainFixture.DeployerKey));
        return Convert.FromHexString(signature[2..]);
    }

    private static Task<T> Call<T>(Web3 web3, string address, string function, params object[] args) =>
        web3.Eth.GetContract(Abi, address).GetFunction(function).CallAsync<T>(args);

    private static async Task Send(Web3 web3, string address, string function, params object[] args)
    {
        var receipt = await web3.Eth.GetContract(Abi, address).GetFunction(function)
            .SendTransactionAndWaitForReceiptAsync(web3.TransactionManager.Account.Address, new Nethereum.Hex.HexTypes.HexBigInteger(3_000_000), null, null, args);
        Assert.Equal(BigInteger.One, receipt.Status.Value);
    }

    private async Task SetNextBlockTimestamp(ulong timestamp)
    {
        using var http = new HttpClient();
        foreach (var body in new[]
                 {
                     $$"""{"jsonrpc":"2.0","method":"evm_setNextBlockTimestamp","params":[{{timestamp}}],"id":1}""",
                     """{"jsonrpc":"2.0","method":"evm_mine","params":[],"id":2}""",
                 })
        {
            (await http.PostAsync(chain.RpcUrl, new StringContent(body, System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        }
    }
}
