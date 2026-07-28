// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title IStoragePenaltyStateMachine
/// @notice Public interface for issue #82's storage-node telemetry-driven
/// progressive penalty state machine (Stage 0 Healthy .. Stage 4 Removed),
/// analogous in shape to the compute-node variant (issue #91) but with its
/// own storage-specific trigger set (missed heartbeats, checksum error
/// rate, degraded I/O throughput, failed PoR challenges, confirmed
/// partial/permanent redundancy loss) from
/// `node-runner-incentives-and-penalties.md`'s Penalties and Slashing
/// section. Kept separate from the concrete contract so future consumers
/// (#80's payout engine, #87's alerting) depend only on the call shapes
/// they need, matching the narrow-interface pattern already established by
/// `INodeRegistry` / `ISlashingController`.
interface IStoragePenaltyStateMachine {
    enum Stage {
        Stage0Healthy,
        Stage1Warning,
        Stage2Degraded,
        Stage3Suspended,
        Stage4Removed
    }

    /// @notice Trigger categories, used as the `triggerType` payload on
    /// `StageTransition` / `SlashApplied` events and to select the
    /// human-readable recommendation string.
    enum TriggerType {
        None,
        Heartbeat,
        ChecksumErrorRate,
        IoDegradation,
        PorFailure,
        PartialDataLoss,
        PermanentDataLoss,
        MissingTelemetry,
        Recovery
    }

    event StageTransition(
        address indexed node,
        Stage previousStage,
        Stage newStage,
        uint256 indexed epoch,
        TriggerType triggerType,
        string action,
        string recommendation,
        uint8 severity
    );

    event SlashApplied(
        address indexed node,
        uint256 indexed epoch,
        Stage stage,
        uint256 amount,
        TriggerType triggerType,
        string recommendation,
        uint8 severity
    );

    event TenantDataLossNotification(address indexed node, uint256 indexed epoch, bytes32 evidenceRef);

    function submitTelemetry(
        address node,
        uint256 epoch,
        uint256 heartbeatMisses,
        uint256 checksumErrorRateBps,
        uint256 ioDegradationBps,
        bool porFailed,
        bool porAuditPassed
    ) external;

    function sealEpoch(address node, uint256 epoch) external;

    function reportPartialDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external;

    function reportPermanentDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external;

    function stagePenaltyMult(address node, uint256 epoch) external view returns (uint256);

    function dealEligible(address node) external view returns (bool);

    function currentStageOf(address node) external view returns (Stage);
}
