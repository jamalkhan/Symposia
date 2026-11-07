using Nethereum.Signer;
using Symposia.Blockchain.Gateway.Tests;
using Symposia.NodeHost.Components;

namespace Symposia.NodeHost.Tests;

/// <summary>Issue #181: LocalDev's rewards profile and the check that spots a chain deployed without it.</summary>
public sealed class LocalDevRewardsTests(RewardsChainFixture chain) : IClassFixture<RewardsChainFixture>
{
    private const string Node = "0x1c44aCA2D907aDEA8BC6463875f8e6442098C3f8";

    [Fact]
    public void Profile_PassesOneHourTimingAndThisNodeAsCommittee()
    {
        var env = new LocalDevRewardsProfile(Node).ToEnvironment();

        Assert.Equal("3600", env["EPOCH_LENGTH"]);
        Assert.Equal("300", env["EPOCH_FINALITY_WINDOW"]);
        Assert.Equal("4", env["TELEMETRY_CHECKPOINTS_PER_EPOCH"]);
        Assert.Equal("300", env["TELEMETRY_COMMIT_WINDOW"]);
        Assert.Equal("3600", env["REWARD_RECORDS_MAX_WAIT"]);
        Assert.Equal(Node, env["TELEMETRY_COMMITTEE"]);
        Assert.Equal("1", env["TELEMETRY_THRESHOLD"]);
    }

    [Fact]
    public void Profile_FitsWholeHeartbeatsInEveryWindow()
    {
        var p = new LocalDevRewardsProfile(Node);

        // Same rule DeployRewards.validateTiming enforces on-chain.
        Assert.Equal(0u, p.EpochLengthSeconds % (p.CheckpointsPerEpoch * p.HeartbeatCadenceSeconds));
        Assert.True(p.SelfCheckpointGraceSeconds < p.FinalityWindowSeconds + p.CommitWindowSeconds);
    }

    [Fact]
    public async Task ChainWithProductionTiming_AndAnotherCommittee_IsStale()
    {
        // The fixture deploys DeployRewards with its defaults: 1-day epochs, the deployer as committee.
        var notice = await ChainComponent.CheckRewardsProfileAsync(chain.RpcUrl, Deployment(), new LocalDevRewardsProfile(Node), default);

        Assert.NotNull(notice);
        Assert.Equal(86_400UL, notice.OnChainEpochSeconds);
        Assert.Contains("86400s epochs", notice.Reason);
        Assert.Contains("not this node", notice.Reason);
    }

    [Fact]
    public async Task ChainMatchingTheProfile_IsNotStale()
    {
        var deployer = new EthECKey(RewardsChainFixture.DeployerKey).GetPublicAddress();
        var profile = new LocalDevRewardsProfile(deployer) { EpochLengthSeconds = 86_400 };

        Assert.Null(await ChainComponent.CheckRewardsProfileAsync(chain.RpcUrl, Deployment(), profile, default));
    }

    private ChainComponent.Deployment Deployment() => new("0xnodes", "0xroots", chain.RpcUrl, 31337,
        EpochControllerAddress: chain.Addresses["EpochController"], TelemetryRegistryAddress: chain.Addresses["TelemetryCheckpointRegistry"]);
}
