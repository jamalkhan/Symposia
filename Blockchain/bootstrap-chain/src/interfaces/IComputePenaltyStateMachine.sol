// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title IComputePenaltyStateMachine
/// @notice Public interface for issue #91's compute-node telemetry-driven
/// progressive penalty state machine (Stage 0 Healthy .. Stage 4 Removed).
/// Kept separate from the concrete contract so future consumers (#90's
/// placement consumer, #92's live-migration executor) depend only on the
/// call shapes they need, matching the narrow-interface pattern already
/// established by `INodeRegistry` / `ISlashingController`.
interface IComputePenaltyStateMachine {
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
        Latency,
        RestartRate,
        WalLag,
        MemoryPressure,
        DatabaseUnavailable,
        DataLoss,
        MissingTelemetry,
        Recovery
    }

    /// @notice Migration urgency accompanying a `MigrationSignal` (Stage 2 =
    /// Candidate, Stage 3 = Required).
    enum MigrationUrgency {
        Candidate,
        Required
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

    event MigrationSignal(
        address indexed node, uint256 indexed epoch, MigrationUrgency urgency, uint256 hostedDatabaseCount
    );

    event TenantDataLossNotification(address indexed node, uint256 indexed epoch, bytes32 evidenceRef);

    function submitTelemetry(
        address node,
        uint256 epoch,
        uint256 p99LatencyMs,
        uint256 restartCount,
        uint256 walLagSeconds,
        uint256 memoryPressureEvents,
        uint256 uptimeBps,
        uint256 hostedDatabaseCount,
        uint256 tierMedianP99LatencyMs,
        uint256 tierCohortSize
    ) external;

    function sealEpoch(address node, uint256 epoch) external;

    function reportDatabaseUnavailable(address node, uint256 epoch, uint256 hostedDatabaseCount, bytes32 evidenceRef)
        external;

    function reportDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external;

    function stagePenaltyMult(address node, uint256 epoch) external view returns (uint256);

    function placementEligible(address node) external view returns (bool);

    function currentStageOf(address node) external view returns (Stage);
}
