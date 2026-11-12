using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using Microsoft.AspNetCore.Mvc.Testing;
using Nethereum.Web3;
using Symposia.Blockchain.Contracts;
using Symposia.Network.Node.Rewards;
using Wire = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// #221: a Blob Gateway report signed by the LocalDev self-signer (the deployer, which DeployRewards makes the
/// metric signer) is accepted by the real RewardDistributor through the relay, so the C# digest matches the
/// contract's. Bad signatures and factor counts come back 409, terminal, and spend nothing.
/// </summary>
public sealed class ServiceMetricsRelayChainTests(RewardsChainFixture chain) : IClassFixture<RewardsChainFixture>
{
    private const byte BlobGateway = 8;
    private const string Node = "0x90F79bf6EB2c4f870365E785982E1f101E93b906"; // anvil #3: unregistered, so accountOf is the node

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

    private async Task<ulong> CurrentEpochAsync() => (ulong)await new Web3(chain.RpcUrl).Eth.GetContractQueryHandler<CurrentEpochFunction>()
        .QueryAsync<BigInteger>(chain.Addresses["EpochController"], new CurrentEpochFunction());

    private MetricReport Report(ulong epoch, params long[] values) =>
        new(chain.Addresses["RewardDistributor"], Node, BlobGateway, epoch, [.. values.Select(v => new BigInteger(v))], 9_500);

    [Fact]
    public async Task SelfSignedReport_IsAcceptedAndMarksTheEpochSubmitted()
    {
        await using var gateway = Gateway();
        var epoch = await CurrentEpochAsync();
        var report = Report(epoch, 9_500, 9_990, 42, 1_000_000);
        var signer = new LocalDevSelfSigner(RewardsChainFixture.DeployerKey, 31337);

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.Path, Wire.Request.From(report, await signer.AttestAsync(report, default)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("0x", (await response.Content.ReadFromJsonAsync<Wire.Response>())!.TransactionHash);
        var record = await new Web3(chain.RpcUrl).Eth.GetContractQueryHandler<MetricOfFunction>()
            .QueryDeserializingToObjectAsync<MetricRecordOutput>(new MetricOfFunction { Epoch = epoch, Account = Node }, chain.Addresses["RewardDistributor"]);
        Assert.True(record.Submitted);
        Assert.Equal(new BigInteger(9_500), record.HeartbeatBps);
    }

    [Fact]
    public async Task WrongSigner_Is409InvalidSignature()
    {
        await using var gateway = Gateway();
        var report = Report(await CurrentEpochAsync(), 1, 2, 3, 4);
        var stranger = new LocalDevSelfSigner(RewardsChainFixture.NodeKey, 31337);

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.Path, Wire.Request.From(report, await stranger.AttestAsync(report, default)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("InvalidSignature", (await response.Content.ReadFromJsonAsync<Wire.Response>())!.Error);
    }

    [Fact]
    public async Task WrongFactorCount_Is409FactorCountMismatch()
    {
        await using var gateway = Gateway();
        var report = Report(await CurrentEpochAsync(), 1, 2, 3);
        var signer = new LocalDevSelfSigner(RewardsChainFixture.DeployerKey, 31337);

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.Path, Wire.Request.From(report, await signer.AttestAsync(report, default)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("FactorCountMismatch", (await response.Content.ReadFromJsonAsync<Wire.Response>())!.Error);
    }

    [Fact]
    public async Task MalformedReport_Is400()
    {
        await using var gateway = Gateway();

        var response = await gateway.CreateClient().PostAsJsonAsync(Wire.Path, new Wire.Request(Node, BlobGateway, 0, ["-1"], 0, "0x00"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void SelfSigner_RefusesAnyChainButLocalDev()
    {
        var ex = Assert.Throws<MetricSignerConfigurationException>(() => new LocalDevSelfSigner(RewardsChainFixture.DeployerKey, 84532));
        Assert.Contains("only runs on LocalDev", ex.Message);
    }

    [Fact]
    public async Task Digest_RecoversToTheSigner()
    {
        var signer = new LocalDevSelfSigner(RewardsChainFixture.DeployerKey, 31337);
        var report = Report(7, 1, 2, 3, 4);

        var attestation = await signer.AttestAsync(report, default);

        Assert.Equal(signer.Address, MetricReportDigest.Recover(report, attestation.Signature), ignoreCase: true);
        Assert.NotEqual(signer.Address, MetricReportDigest.Recover(report with { Epoch = 8 }, attestation.Signature), StringComparer.OrdinalIgnoreCase);
    }
}
