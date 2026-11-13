using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using Microsoft.AspNetCore.Mvc.Testing;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Signer;
using Nethereum.Web3;
using Symposia.Blockchain.Contracts;
using Wire = Symposia.Network.Node.Rewards.VerifierAttestationWire;
using Response = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire.Response;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// #227 against the real DeployRewards stack: the C# attestation digest equals the contract's, the deployed config
/// points at VerifierAssignments, and the relay refuses forged or unseeded batches without spending gas.
/// </summary>
public sealed class VerifierRelayChainTests(RewardsChainFixture chain) : IClassFixture<RewardsChainFixture>
{
    [Function("attestationDigest", "bytes32")]
    private sealed class AttestationDigestFunction : FunctionMessage
    {
        [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
        [Parameter("address", "verifier", 2)] public string Verifier { get; set; } = "";
        [Parameter("tuple[]", "items", 3)] public List<AttestationItem> Items { get; set; } = [];
    }

    private WebApplicationFactory<Program> Gateway() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Gateway:RpcUrl"] = chain.RpcUrl,
            ["Gateway:RelayerPrivateKey"] = RewardsChainFixture.DeployerKey,
            ["Gateway:RewardDistributorAddress"] = chain.Addresses["RewardDistributor"],
            ["Gateway:EpochControllerAddress"] = chain.Addresses["EpochController"],
            ["Gateway:EpochSealer:Enabled"] = "false",
        })
        {
            b.UseSetting(key, value);
        }
    });

    private static List<Wire.ItemWire> Items() =>
        [new("0x90F79bf6EB2c4f870365E785982E1f101E93b906", 0, ["10000", "9950", "42", "123456"]), new("0x15d34AAf54267DB7D7c367839AAf71A00a2C6A65", 2, ["1", "2", "3", "4"])];

    [Fact]
    public async Task Digest_MatchesTheContract()
    {
        var verifier = new EthECKey(RewardsChainFixture.NodeKey).GetPublicAddress();
        var contract = chain.Addresses["VerifierAssignments"];

        var onChain = await new Web3(chain.RpcUrl).Eth.GetContractQueryHandler<AttestationDigestFunction>()
            .QueryAsync<byte[]>(contract, new AttestationDigestFunction { Epoch = 7, Verifier = verifier, Items = Wire.ToItems(Items()) });

        Assert.Equal(onChain, AttestationDigest.Hash(contract, 31337, 7, verifier, Wire.ToItems(Items())));
    }

    [Fact]
    public async Task DeployedConfig_PointsAtVerifierAssignments()
    {
        var web3 = new Web3(chain.RpcUrl);
        var config = await web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(chain.Addresses["RewardDistributor"], new ConfigFunction());
        var address = await web3.Eth.GetContractQueryHandler<GetAddressFunction>().QueryAsync<string>(config, new GetAddressFunction { Key = ConfigKeys.VerifierAssignments });

        Assert.Equal(chain.Addresses["VerifierAssignments"], address, ignoreCase: true);
    }

    [Fact]
    public async Task ForgedBatch_Is409InvalidSignature()
    {
        await using var gateway = Gateway();
        var verifier = new EthECKey(RewardsChainFixture.NodeKey).GetPublicAddress();
        var forged = Wire.Sign(EthECKey.GenerateKey(), chain.Addresses["VerifierAssignments"], 31337, 7, Items());

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.Path, new Wire.Request(7, verifier, Items(), forged));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("InvalidSignature", (await response.Content.ReadFromJsonAsync<Response>())!.Error);
    }

    [Fact]
    public async Task UnseededEpoch_Is409NotSeeded()
    {
        await using var gateway = Gateway();
        var key = new EthECKey(RewardsChainFixture.NodeKey);
        var signature = Wire.Sign(key, chain.Addresses["VerifierAssignments"], 31337, 999, Items());

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.Path, new Wire.Request(999, key.GetPublicAddress(), Items(), signature));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NotSeeded", (await response.Content.ReadFromJsonAsync<Response>())!.Error);
    }

    [Fact]
    public async Task SelfSync_IsRelayed()
    {
        await using var gateway = Gateway();

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.SyncPath, new Wire.SyncRequest(new EthECKey(RewardsChainFixture.NodeKey).GetPublicAddress()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
