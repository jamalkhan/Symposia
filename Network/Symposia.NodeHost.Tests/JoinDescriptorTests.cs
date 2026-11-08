using Symposia.NodeHost.Components;

namespace Symposia.NodeHost.Tests;

/// <summary>Issue #172: a joined host takes the rewards stack and an RPC from the gateway's descriptor.</summary>
public sealed class JoinDescriptorTests
{
    private static readonly Uri Gateway = new("http://10.0.0.213:5180");

    private static ChainComponent.GatewayNetwork Descriptor(string? rpcUrl = "http://10.0.0.213:5180/v1/rpc") =>
        new(31337, "0xnodes", "0xroots", "0xactivations", "0xrewards", "0xepochs", "0xtelemetry", "0xstaking", rpcUrl, 42, 2_000);

    [Fact]
    public void FullDescriptor_MapsEveryContract_AndReadsThroughTheGateway()
    {
        var network = ChainComponent.FromDescriptor(Gateway, Descriptor());

        Assert.Equal("http://10.0.0.213:5180/v1/rpc", network.RpcUrl);
        Assert.Equal(RpcSource.Gateway, network.RpcSource);
        Assert.Equal("0xrewards", network.RewardDistributorAddress);
        Assert.Equal("0xepochs", network.EpochControllerAddress);
        Assert.Equal("0xtelemetry", network.TelemetryRegistryAddress);
        Assert.Equal("0xstaking", network.StakingRegistryAddress);
        Assert.Equal(42UL, network.DeployBlock);
        Assert.Equal(2_000UL, network.MaxLogBlockRange); // the proxy's cap, so log scans page under it
    }

    [Fact]
    public void RelativeRpcUrl_ResolvesAgainstTheGateway()
    {
        Assert.Equal("http://10.0.0.213:5180/v1/rpc", ChainComponent.FromDescriptor(Gateway, Descriptor("/v1/rpc")).RpcUrl);
    }

    [Fact]
    public void OperatorRpc_IsMarkedAdvertised()
    {
        var network = ChainComponent.FromDescriptor(Gateway, Descriptor("https://rpc.example.org"));

        Assert.Equal(RpcSource.Advertised, network.RpcSource);
        Assert.Null(network.MaxLogBlockRange); // the gateway's cap says nothing about someone else's RPC
    }

    [Theory]
    [InlineData("ftp://rpc.example.org")]
    [InlineData("   ")]
    public void UnusableRpcUrl_IsIgnored(string rpcUrl)
    {
        Assert.Null(ChainComponent.FromDescriptor(Gateway, Descriptor(rpcUrl)).RpcUrl);
    }

    [Fact]
    public void OlderGateway_LeavesReadsAndRewardsUnavailable()
    {
        var network = ChainComponent.FromDescriptor(Gateway, new ChainComponent.GatewayNetwork(31337, "0xnodes", "0xroots"));

        Assert.Null(network.RpcUrl);
        Assert.Null(network.RewardDistributorAddress);
        Assert.Null(network.TelemetryRegistryAddress);
        Assert.Equal(0UL, network.DeployBlock);
    }
}
