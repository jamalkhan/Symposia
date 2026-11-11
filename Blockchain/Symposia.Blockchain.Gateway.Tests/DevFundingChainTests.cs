using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.Web3;
using Symposia.Blockchain.Contracts;
using Symposia.Network.Node;
using Symposia.Network.Node.Staking;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// LocalDev self-staking (issue #200) against a real anvil chain with the rewards stack deployed: a
/// fresh node with no ETH or SYM funds itself through the gateway, stakes with its own key through the
/// gateway's RPC proxy, and is verified to Active. The fixture's deployer key is the dev minter and
/// verifier, as in LocalDev.
/// </summary>
public sealed class DevFundingChainTests(RewardsChainFixture chain) : IClassFixture<RewardsChainFixture>, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym200-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private WebApplicationFactory<Program> Gateway(bool devFunding) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        // UseSetting, not ConfigureAppConfiguration: GatewayApp reads these while building.
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Gateway:RpcUrl"] = chain.RpcUrl,
            ["Gateway:RelayerPrivateKey"] = RewardsChainFixture.DeployerKey,
            ["Gateway:StakingRegistryAddress"] = chain.Addresses["StakingNodeRegistry"],
            ["Gateway:EpochControllerAddress"] = chain.Addresses["EpochController"],
            ["Gateway:DevFunding:Enabled"] = devFunding.ToString(),
            ["Gateway:EpochSealer:Enabled"] = "false",
            ["Gateway:RpcProxy:RateLimit:Enabled"] = "false",
        })
        {
            b.UseSetting(key, value);
        }
    });

    private NodeIdentity NewNode()
    {
        Directory.CreateDirectory(_dir);
        var identity = new NodeIdentity(Options.Create(new NodeNetworkOptions { NodeIdentityKeyPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".key") }));
        identity.EnsureLoadedOrGenerated();
        return identity;
    }

    private NodeServiceStaker Staker(WebApplicationFactory<Program> gateway, NodeIdentity node) =>
        new(node, DevFunding.LocalDevChainId, new Uri(gateway.Server.BaseAddress, "/v1/rpc").ToString(), chain.Addresses["StakingNodeRegistry"],
            new DevGatewayStakeFunding(node, DevFunding.LocalDevChainId, gateway.Server.BaseAddress, gateway.CreateClient()), NullLogger.Instance,
            gateway.CreateClient());

    private static async Task<StakeOutcome> StakeAsync(NodeServiceStaker staker, byte service) =>
        (await staker.EnsureStakedAsync([(service, BigInteger.One)], default))[service];

    private async Task<ServiceStatus> StatusAsync(string node, byte service) =>
        (ServiceStatus)await new Web3(chain.RpcUrl).Eth.GetContractQueryHandler<ServiceStatusOfFunction>()
            .QueryAsync<byte>(chain.Addresses["StakingNodeRegistry"], new ServiceStatusOfFunction { Node = node, Service = service });

    [Fact]
    public async Task FreshNode_FundsStakesAndIsVerifiedActive_ThenRepeatsAreNoOps()
    {
        await using var gateway = Gateway(devFunding: true);
        var node = NewNode();
        var staker = Staker(gateway, node);

        Assert.Equal(StakeOutcome.Active, await StakeAsync(staker, 0));
        Assert.Equal(ServiceStatus.Active, await StatusAsync(node.NodeId, 0));

        var stake = await new Web3(chain.RpcUrl).Eth.GetContractQueryHandler<MinStakeForFunction>()
            .QueryAsync<BigInteger>(chain.Addresses["StakingNodeRegistry"], new MinStakeForFunction { NodeType = 0, Capacity = BigInteger.One });
        var held = await new Web3(chain.RpcUrl).Eth.GetContractQueryHandler<BalanceOfFunction>()
            .QueryAsync<BigInteger>(chain.Addresses["SYMToken"], new BalanceOfFunction { Account = node.NodeId });
        Assert.Equal(stake, held); // granted 2x the stake, staked 1x

        // A restart (or repeat) reads Active and does nothing.
        Assert.Equal(StakeOutcome.Active, await StakeAsync(staker, 0));
        Assert.Equal(held, await new Web3(chain.RpcUrl).Eth.GetContractQueryHandler<BalanceOfFunction>()
            .QueryAsync<BigInteger>(chain.Addresses["SYMToken"], new BalanceOfFunction { Account = node.NodeId }));
    }

    // Review (Lenny #1): one host's services stake together from one account without tripping over
    // each other's allowance or nonce, funded by a single grant.
    [Fact]
    public async Task SeveralServicesOnOneNode_AllReachActive()
    {
        await using var gateway = Gateway(devFunding: true);
        var node = NewNode();

        var outcomes = await Staker(gateway, node).EnsureStakedAsync([(0, BigInteger.One), (8, BigInteger.Zero)], default);

        Assert.Equal(StakeOutcome.Active, outcomes[0]);
        Assert.Equal(StakeOutcome.Active, outcomes[8]);
        Assert.Equal(ServiceStatus.Active, await StatusAsync(node.NodeId, 8));
    }

    [Fact]
    public async Task DevFundingOn_TheProxyAcceptsSignedTransactions()
    {
        await using var gateway = Gateway(devFunding: true);
        var send = await gateway.CreateClient().PostAsJsonAsync("/v1/rpc",
            new { jsonrpc = "2.0", id = 1, method = "eth_sendRawTransaction", @params = new[] { "0x00" } });
        Assert.DoesNotContain("not available", await send.Content.ReadAsStringAsync()); // forwarded (anvil rejects the junk itself)
    }

    [Fact]
    public async Task DevFundingOff_EndpointsAreNotMapped_AndTheProxyStaysReadOnly()
    {
        await using var gateway = Gateway(devFunding: false);
        var node = NewNode();

        Assert.Equal(StakeOutcome.Unavailable, await StakeAsync(Staker(gateway, node), 0));
        var send = await gateway.CreateClient().PostAsJsonAsync("/v1/rpc",
            new { jsonrpc = "2.0", id = 1, method = "eth_sendRawTransaction", @params = new[] { "0x00" } });
        Assert.Contains("not available", await send.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ForgedOrStaleSignatures_AreRefused_AndMoveNoFunds()
    {
        await using var gateway = Gateway(devFunding: true);
        var victim = NewNode();
        var attacker = NewNode();
        var client = gateway.CreateClient();
        var services = new[] { new DevFundService(0, "1") };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Signed by another key for the victim's address.
        var forged = await client.PostAsJsonAsync(DevFunding.FundPath, new DevFundRequest(victim.NodeId, services, now,
            attacker.SignDevFunding(DevFunding.FundDigest(DevFunding.LocalDevChainId, victim.NodeId, services, now))));
        // Signed correctly, but too old.
        var old = now - 600;
        var stale = await client.PostAsJsonAsync(DevFunding.FundPath, new DevFundRequest(victim.NodeId, services, old,
            victim.SignDevFunding(DevFunding.FundDigest(DevFunding.LocalDevChainId, victim.NodeId, services, old))));

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode); // a valid signature, by the wrong key
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(BigInteger.Zero, (await new Web3(chain.RpcUrl).Eth.GetBalance.SendRequestAsync(victim.NodeId)).Value);
    }

    [Fact]
    public async Task Verify_RefusesUnregisteredServices()
    {
        await using var gateway = Gateway(devFunding: true);
        var node = NewNode();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var response = await gateway.CreateClient().PostAsJsonAsync(DevFunding.VerifyPath, new DevVerifyRequest(node.NodeId, 0, now,
            node.SignDevFunding(DevFunding.VerifyDigest(DevFunding.LocalDevChainId, node.NodeId, 0, now))));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
