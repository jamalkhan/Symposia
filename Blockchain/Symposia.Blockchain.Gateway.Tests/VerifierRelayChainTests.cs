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
        [Parameter("uint256", "nonce", 3)] public BigInteger Nonce { get; set; }
        [Parameter("tuple[]", "items", 4)] public List<AttestationItem> Items { get; set; } = [];
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
            .QueryAsync<byte[]>(contract, new AttestationDigestFunction { Epoch = 7, Verifier = verifier, Nonce = 42, Items = Wire.ToItems(Items()) });

        Assert.Equal(onChain, AttestationDigest.Hash(contract, 31337, 7, verifier, 42, Wire.ToItems(Items())));
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
        var forged = Wire.Sign(EthECKey.GenerateKey(), chain.Addresses["VerifierAssignments"], 31337, 7, 1, Items());

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.Path, new Wire.Request(7, verifier, 1, Items(), forged));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("InvalidSignature", (await response.Content.ReadFromJsonAsync<Response>())!.Error);
    }

    [Fact]
    public async Task UnseededEpoch_Is409NotSeeded()
    {
        await using var gateway = Gateway();
        var key = new EthECKey(RewardsChainFixture.NodeKey);
        var signature = Wire.Sign(key, chain.Addresses["VerifierAssignments"], 31337, 999, 1, Items());

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.Path, new Wire.Request(999, key.GetPublicAddress(), 1, Items(), signature));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NotSeeded", (await response.Content.ReadFromJsonAsync<Response>())!.Error);
    }

    [Fact]
    public async Task SelfSync_OfANodeAlreadyInSync_SendsNothing()
    {
        await using var gateway = Gateway();

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.SyncPath, new Wire.SyncRequest(new EthECKey(RewardsChainFixture.NodeKey).GetPublicAddress()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<Response>())!.TransactionHash); // unregistered and not pooled: nothing stale
    }

    [Fact]
    public async Task Relay_IsRateLimitedPerVerifier()
    {
        await using var gateway = Gateway();
        var client = gateway.CreateClient();
        var key = EthECKey.GenerateKey();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
        {
            var signature = Wire.Sign(key, chain.Addresses["VerifierAssignments"], 31337, 999, (ulong)i + 1, Items());
            statuses.Add((await client.PostAsJsonAsync(Wire.Path, new Wire.Request(999, key.GetPublicAddress(), (ulong)i + 1, Items(), signature))).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
