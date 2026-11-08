namespace Symposia.NodeHost.Components;

/// <summary>
/// The rewards timing and committee a LocalDev chain is deployed with (issue #181), so the real
/// telemetry committee can settle an epoch on one machine in about 75 minutes. The values feed
/// DeployRewards.s.sol, which applies the same validation testnet and mainnet deploys get; those
/// networks keep the script's production defaults.
/// </summary>
/// <param name="CommitteeAddress">This host's node address: the solo committee member, threshold 1.</param>
public sealed record LocalDevRewardsProfile(string CommitteeAddress)
{
    public uint EpochLengthSeconds { get; init; } = 3600;

    public uint FinalityWindowSeconds { get; init; } = 300;

    /// <summary>4 windows of 15 minutes, 3 heartbeats each: one missed heartbeat never empties a window.</summary>
    public uint CheckpointsPerEpoch { get; init; } = 4;

    public uint HeartbeatCadenceSeconds { get; init; } = 300;

    public uint CommitWindowSeconds { get; init; } = 300;

    public uint SelfCheckpointGraceSeconds { get; init; } = 300;

    /// <summary>A stalled committee shows up within an hour instead of a day.</summary>
    public uint RecordsMaxWaitSeconds { get; init; } = 3600;

    public IReadOnlyDictionary<string, string> ToEnvironment() => new Dictionary<string, string>
    {
        ["EPOCH_LENGTH"] = EpochLengthSeconds.ToString(),
        ["EPOCH_FINALITY_WINDOW"] = FinalityWindowSeconds.ToString(),
        ["TELEMETRY_CHECKPOINTS_PER_EPOCH"] = CheckpointsPerEpoch.ToString(),
        ["HEARTBEAT_CADENCE"] = HeartbeatCadenceSeconds.ToString(),
        ["TELEMETRY_COMMIT_WINDOW"] = CommitWindowSeconds.ToString(),
        ["TELEMETRY_SELF_CHECKPOINT_GRACE"] = SelfCheckpointGraceSeconds.ToString(),
        ["REWARD_RECORDS_MAX_WAIT"] = RecordsMaxWaitSeconds.ToString(),
        ["TELEMETRY_COMMITTEE"] = CommitteeAddress,
        ["TELEMETRY_THRESHOLD"] = "1",
    };
}

/// <summary>Why the local chain's rewards stack can't settle with this host (issue #181 FR4).</summary>
public sealed record RewardsStaleNotice(string Reason, ulong OnChainEpochSeconds, IReadOnlyList<string> OnChainCommittee,
    ulong ExpectedEpochSeconds, string ExpectedCommittee);
