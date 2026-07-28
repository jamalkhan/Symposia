// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";
import {ISlashingController} from "./interfaces/ISlashingController.sol";
import {ComputeTierRegistry} from "./ComputeTierRegistry.sol";
import {IComputePenaltyStateMachine} from "./interfaces/IComputePenaltyStateMachine.sol";
import {IDataLossOracle} from "./interfaces/IDataLossOracle.sol";
import {IComputeFailureOracle} from "./interfaces/IComputeFailureOracle.sol";

/// @title ComputePenaltyStateMachine
/// @notice Issue #91: compute-node telemetry-driven progressive penalty
/// state machine (Stage 0 Healthy .. Stage 4 Removed), analogous in shape to
/// `SlashingController`'s hardware-fault Stage 1-4 table but with its own
/// compute-specific trigger set (P99 latency vs. tier peers, restart rate,
/// WAL safekeeper lag, memory pressure, hosted-database unavailability,
/// confirmed data loss) from `compute-nodes.md`'s "Reliability & Fault
/// Handling" table. Self-contained per the Arch pass on #91 -- issue #82's
/// storage-node penalty state machine does not exist yet, so this is not
/// extracted into a shared core, but is written with a distinct
/// trigger-evaluation layer (`_evaluateTriggers`) and table-driven stage
/// transitions so a future extraction is straightforward.
///
/// `stagePenaltyMult` is applied multiplicatively ON TOP of
/// `compute-nodes.md`'s separately-computed 5-factor weighted reward score
/// and tier multiplier -- this contract does not compute or touch that
/// score itself.
contract ComputePenaltyStateMachine is GovernedUpgradeable, IComputePenaltyStateMachine {
    struct Telemetry {
        uint256 p99LatencyMs;
        uint256 restartCount;
        uint256 walLagSeconds;
        uint256 memoryPressureEvents;
        uint256 uptimeBps;
        uint256 hostedDatabaseCount;
        uint256 tierMedianP99LatencyMs;
        uint256 tierCohortSize;
        bool reported;
    }

    struct ComputePenaltyState {
        Stage stage;
        uint256 troubleEpochCount;
        uint256 cleanEpochStreak;
        bool placementHoldActive;
        uint8 placementHoldCleanEpoch; // 0/1, per spec struct field
        uint256 stage3EpisodeId;
        uint256 stage3EntryStake;
        uint256 stage3CumulativeSlashedBps;
        uint256 stage4Base;
        TriggerType lastTriggerType;
        uint256 lastEvaluatedEpoch;
        bool everEvaluated;
    }

    /// @custom:storage-location erc7201:symposia.ComputePenaltyStateMachine
    struct ComputePenaltyStorage {
        mapping(address => ComputePenaltyState) states;
        mapping(address => mapping(uint256 => Telemetry)) telemetry;
        mapping(address => mapping(uint256 => bool)) sealedEpoch;
        mapping(address => mapping(uint256 => uint256)) epochMultiplierBps;
    }

    bytes32 private constant COMPUTE_PENALTY_STORAGE_LOCATION = keccak256("symposia.storage.ComputePenaltyStateMachine");

    error NotNode(address caller, address node);
    error AlreadySealed(address node, uint256 epoch);
    error OutOfOrderEpoch(uint256 provided, uint256 expected);
    error DataLossNotConfirmed(address node, uint256 epoch);
    error DatabaseUnavailabilityNotConfirmed(address node, uint256 epoch);

    function _storage() private pure returns (ComputePenaltyStorage storage $) {
        bytes32 slot = COMPUTE_PENALTY_STORAGE_LOCATION;
        assembly {
            $.slot := slot
        }
    }

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        _disableInitializers();
    }

    function initialize(IProtocolConfig initialConfig) external initializer {
        __GovernedUpgradeable_init(initialConfig);
    }

    function _registry() internal view returns (INodeRegistry) {
        return INodeRegistry(config().getAddress(ConfigKeys.REGISTRY_ADDRESS));
    }

    function _slashingController() internal view returns (ISlashingController) {
        return ISlashingController(config().getAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER));
    }

    function _tierRegistry() internal view returns (ComputeTierRegistry) {
        return ComputeTierRegistry(config().getAddress(ConfigKeys.COMPUTE_PENALTY_TIER_REGISTRY_ADDRESS));
    }

    // --- Views ---

    function stateOf(address node) external view returns (ComputePenaltyState memory) {
        return _storage().states[node];
    }

    function currentStageOf(address node) external view returns (Stage) {
        return _storage().states[node].stage;
    }

    function telemetryOf(address node, uint256 epoch) external view returns (Telemetry memory) {
        return _storage().telemetry[node][epoch];
    }

    function isSealed(address node, uint256 epoch) external view returns (bool) {
        return _storage().sealedEpoch[node][epoch];
    }

    /// @notice Reward multiplier (bps out of 10_000) locked in at the moment
    /// `epoch` was sealed for `node`. Immutable once sealed; returns 0 for
    /// an epoch that has not yet been sealed (same "unset == 0" convention
    /// `IProtocolConfig` uses).
    function stagePenaltyMult(address node, uint256 epoch) external view returns (uint256) {
        return _storage().epochMultiplierBps[node][epoch];
    }

    /// @notice Stage 3+ blocks new placements. After recovering out of a
    /// stage, the node needs one additional clean epoch beyond the exit
    /// epoch before becoming eligible again.
    function placementEligible(address node) external view returns (bool) {
        ComputePenaltyState storage s = _storage().states[node];
        if (s.stage != Stage.Stage0Healthy) return false;
        if (!s.placementHoldActive) return true;
        return s.placementHoldCleanEpoch == 1;
    }

    // --- Telemetry submission (self-reported) ---

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
    ) external whenNotPaused {
        if (msg.sender != node) revert NotNode(msg.sender, node);
        if (_storage().sealedEpoch[node][epoch]) revert AlreadySealed(node, epoch);

        _storage().telemetry[node][epoch] = Telemetry({
            p99LatencyMs: p99LatencyMs,
            restartCount: restartCount,
            walLagSeconds: walLagSeconds,
            memoryPressureEvents: memoryPressureEvents,
            uptimeBps: uptimeBps,
            hostedDatabaseCount: hostedDatabaseCount,
            tierMedianP99LatencyMs: tierMedianP99LatencyMs,
            tierCohortSize: tierCohortSize,
            reported: true
        });
    }

    // --- Per-epoch evaluation / seal (telemetry-driven path) ---

    /// @notice Permissionless keeper entrypoint (mirrors
    /// `StakingNodeRegistry.checkOvercommitment`'s pull-based pattern):
    /// evaluates `node`'s Stage 1/2/3-via-persistence triggers and recovery
    /// progress for `epoch` from whatever telemetry (if any) was submitted,
    /// then seals the epoch. Sealing is a structural idempotency guard --
    /// effects (state writes, the `sealedEpoch` flag itself) are applied
    /// before any external call (checks-effects-interactions), so a
    /// reentrant or duplicate call cannot double-slash.
    function sealEpoch(address node, uint256 epoch) external whenNotPaused {
        ComputePenaltyStorage storage $ = _storage();
        if ($.sealedEpoch[node][epoch]) revert AlreadySealed(node, epoch);
        $.sealedEpoch[node][epoch] = true;

        ComputePenaltyState storage s = $.states[node];
        Stage previousStage = s.stage;

        if (s.stage == Stage.Stage4Removed) {
            // Continuing 5%/epoch of the post-20%-slash flat base while the
            // node remains in Stage 4; not gated on telemetry ordering since
            // a Stage 4 node is expected to stop reporting.
            _applyStage4OngoingSlash(node, epoch, s);
            $.epochMultiplierBps[node][epoch] = _stageMultBps(Stage.Stage4Removed);
            s.lastEvaluatedEpoch = epoch;
            return;
        }

        uint256 expected = s.everEvaluated ? s.lastEvaluatedEpoch + 1 : epoch;
        if (epoch != expected) revert OutOfOrderEpoch(epoch, expected);

        Telemetry storage t = $.telemetry[node][epoch];
        if (!t.reported) {
            // A gap in reporting must not read as "healthy" and must not
            // count toward the clean-epoch recovery streak -- treated the
            // same as a failing epoch, with its own trigger type so alert
            // payloads can distinguish "sensor didn't report" from "sensor
            // reported bad numbers".
            _handleUnhealthyEpoch(node, epoch, s, previousStage, TriggerType.MissingTelemetry, 0);
        } else {
            (bool latencyTrig, bool restartTrig, bool walTrig, bool memTrig) = _evaluateTriggers(node, epoch, t);
            if (memTrig) {
                _bypassToStage2(node, epoch, s, previousStage, TriggerType.MemoryPressure, t.hostedDatabaseCount);
            } else if (latencyTrig || restartTrig || walTrig) {
                TriggerType triggerType =
                    latencyTrig ? TriggerType.Latency : (restartTrig ? TriggerType.RestartRate : TriggerType.WalLag);
                _handleUnhealthyEpoch(node, epoch, s, previousStage, triggerType, t.hostedDatabaseCount);
            } else {
                _handleCleanEpoch(node, epoch, s);
            }
        }

        s.lastEvaluatedEpoch = epoch;
        s.everEvaluated = true;
        $.epochMultiplierBps[node][epoch] = _stageMultBps(s.stage);
    }

    // --- Trigger evaluation layer ---

    function _evaluateTriggers(address node, uint256 epoch, Telemetry storage t)
        internal
        view
        returns (bool latencyTrig, bool restartTrig, bool walTrig, bool memTrig)
    {
        uint256 thresholdMs;
        uint256 minCohort = config().getUint(ConfigKeys.COMPUTE_PENALTY_MIN_COHORT_SIZE);
        if (t.tierCohortSize >= minCohort && t.tierMedianP99LatencyMs > 0) {
            uint256 multBps = config().getUint(ConfigKeys.COMPUTE_PENALTY_LATENCY_MULT_BPS);
            thresholdMs = (t.tierMedianP99LatencyMs * multBps) / 10_000;
        } else {
            uint8 tier = uint8(_tierRegistry().currentTierOf(node));
            thresholdMs = config().getUint(ConfigKeys.computePenaltyAbsLatencyMs(tier));
        }
        latencyTrig = thresholdMs > 0 && t.p99LatencyMs > thresholdMs;

        uint256 restartThreshold = config().getUint(ConfigKeys.COMPUTE_PENALTY_RESTART_THRESHOLD);
        bool overThreshold = t.restartCount > restartThreshold;
        bool risingVsPrevEpoch = false;
        if (epoch > 0) {
            Telemetry storage prev = _storage().telemetry[node][epoch - 1];
            if (prev.reported && t.restartCount > prev.restartCount) {
                risingVsPrevEpoch = true;
            }
        }
        restartTrig = overThreshold || risingVsPrevEpoch;

        uint256 walThreshold = config().getUint(ConfigKeys.COMPUTE_PENALTY_WAL_LAG_THRESHOLD_SECONDS);
        walTrig = t.walLagSeconds > walThreshold;

        memTrig = t.memoryPressureEvents > 0;
    }

    // --- Stage transition table ---

    function _handleUnhealthyEpoch(
        address node,
        uint256 epoch,
        ComputePenaltyState storage s,
        Stage previousStage,
        TriggerType triggerType,
        uint256 hostedDatabaseCount
    ) internal {
        s.cleanEpochStreak = 0;
        s.lastTriggerType = triggerType;
        s.troubleEpochCount += 1;
        // Note: placementHoldActive/placementHoldCleanEpoch are deliberately
        // NOT reset here for a Stage0Healthy node that hasn't yet escalated
        // back into Stage 1 -- a single failing epoch during the post-exit
        // placement hold must further delay eligibility (stay ineligible),
        // not flip it to "no hold in effect" eligible. They are reset to
        // false/0 explicitly inside the escalation branches below, where a
        // real stage transition actually occurs.

        uint256 stage1At = config().getUint(ConfigKeys.COMPUTE_PENALTY_STAGE1_TROUBLE_EPOCHS);
        uint256 stage2At = config().getUint(ConfigKeys.COMPUTE_PENALTY_STAGE2_TROUBLE_EPOCHS);
        uint256 stage3At = config().getUint(ConfigKeys.COMPUTE_PENALTY_STAGE3_TROUBLE_EPOCHS);

        if (s.stage == Stage.Stage0Healthy) {
            if (s.troubleEpochCount >= stage1At) {
                s.placementHoldActive = false;
                s.placementHoldCleanEpoch = 0;
                _transitionTo(
                    node,
                    epoch,
                    s,
                    previousStage,
                    Stage.Stage1Warning,
                    triggerType,
                    "70% reward multiplier applied",
                    _recommendationFor(triggerType),
                    1 /* WARNING */
                );
            }
        } else if (s.stage == Stage.Stage1Warning) {
            if (s.troubleEpochCount >= stage2At) {
                _transitionTo(
                    node,
                    epoch,
                    s,
                    previousStage,
                    Stage.Stage2Degraded,
                    triggerType,
                    "40% reward multiplier applied; hosted databases flagged as migration candidates",
                    _recommendationFor(triggerType),
                    1 /* WARNING */
                );
                _emitMigrationSignal(node, epoch, MigrationUrgency.Candidate, hostedDatabaseCount);
            }
        } else if (s.stage == Stage.Stage2Degraded) {
            if (s.troubleEpochCount >= stage3At) {
                _enterStage3(node, epoch, s, previousStage, triggerType, hostedDatabaseCount);
            }
        } else if (s.stage == Stage.Stage3Suspended) {
            _applyStage3Slash(node, epoch, s, triggerType);
        }
        // Stage4Removed is handled entirely by the early-return branch in
        // `sealEpoch` and never reaches this function.
    }

    function _handleCleanEpoch(address node, uint256 epoch, ComputePenaltyState storage s) internal {
        s.lastTriggerType = TriggerType.Recovery;

        if (s.stage == Stage.Stage0Healthy) {
            // Not yet escalated: a clean epoch resets the consecutive
            // trouble counter so only *consecutive* elevated epochs count
            // toward Stage 1 entry.
            s.troubleEpochCount = 0;
            if (s.placementHoldActive && s.placementHoldCleanEpoch == 0) {
                s.placementHoldCleanEpoch = 1;
                s.placementHoldActive = false;
            }
            return;
        }

        s.cleanEpochStreak += 1;
        uint256 required = config().getUint(ConfigKeys.COMPUTE_PENALTY_RECOVERY_CLEAN_EPOCHS);
        if (s.cleanEpochStreak >= required) {
            Stage previousStage = s.stage;
            s.stage = Stage.Stage0Healthy;
            s.troubleEpochCount = 0;
            s.cleanEpochStreak = 0;
            s.placementHoldActive = true;
            s.placementHoldCleanEpoch = 0;
            emit StageTransition(
                node,
                previousStage,
                Stage.Stage0Healthy,
                epoch,
                TriggerType.Recovery,
                "100% reward multiplier restored; slashed stake not returned",
                "No action needed -- node has recovered",
                0 /* INFO */
            );
        }
    }

    function _bypassToStage2(
        address node,
        uint256 epoch,
        ComputePenaltyState storage s,
        Stage previousStage,
        TriggerType triggerType,
        uint256 hostedDatabaseCount
    ) internal {
        s.cleanEpochStreak = 0;
        s.placementHoldActive = false;
        s.placementHoldCleanEpoch = 0;
        s.lastTriggerType = triggerType;

        if (s.stage == Stage.Stage0Healthy || s.stage == Stage.Stage1Warning) {
            uint256 stage2At = config().getUint(ConfigKeys.COMPUTE_PENALTY_STAGE2_TROUBLE_EPOCHS);
            s.troubleEpochCount = stage2At;
            _transitionTo(
                node,
                epoch,
                s,
                previousStage,
                Stage.Stage2Degraded,
                triggerType,
                "40% reward multiplier applied; hosted databases flagged as migration candidates",
                _recommendationFor(triggerType),
                1 /* WARNING */
            );
            _emitMigrationSignal(node, epoch, MigrationUrgency.Candidate, hostedDatabaseCount);
        }
        // Already Stage2+ -- memory pressure is noted (lastTriggerType) but
        // does not downgrade an already-more-severe stage.
    }

    function _enterStage3(
        address node,
        uint256 epoch,
        ComputePenaltyState storage s,
        Stage previousStage,
        TriggerType triggerType,
        uint256 hostedDatabaseCount
    ) internal {
        bool freshEntry = s.stage != Stage.Stage3Suspended;
        if (freshEntry) {
            s.stage3EpisodeId += 1;
            s.stage3EntryStake = _registry().stakeOf(node);
            s.stage3CumulativeSlashedBps = 0;
        }

        uint256 stage3At = config().getUint(ConfigKeys.COMPUTE_PENALTY_STAGE3_TROUBLE_EPOCHS);
        s.troubleEpochCount = stage3At;
        s.cleanEpochStreak = 0;
        s.placementHoldActive = false;
        s.placementHoldCleanEpoch = 0;
        s.lastTriggerType = triggerType;

        _transitionTo(
            node,
            epoch,
            s,
            previousStage,
            Stage.Stage3Suspended,
            triggerType,
            "0% reward multiplier; node suspended from new placements; hosted databases migrated",
            _recommendationFor(triggerType),
            2 /* CRITICAL */
        );
        _emitMigrationSignal(node, epoch, MigrationUrgency.Required, hostedDatabaseCount);

        if (freshEntry) {
            _applyStage3Slash(node, epoch, s, triggerType);
        }
    }

    function _applyStage3Slash(address node, uint256 epoch, ComputePenaltyState storage s, TriggerType triggerType)
        internal
    {
        uint256 pctBps = config().getUint(ConfigKeys.COMPUTE_PENALTY_STAGE3_PCT_PER_EPOCH_BPS);
        uint256 capBps = config().getUint(ConfigKeys.COMPUTE_PENALTY_STAGE3_CAP_BPS);
        uint256 remainingCap = capBps > s.stage3CumulativeSlashedBps ? capBps - s.stage3CumulativeSlashedBps : 0;
        uint256 appliedBps = pctBps > remainingCap ? remainingCap : pctBps;
        if (appliedBps == 0) return;

        s.stage3CumulativeSlashedBps += appliedBps;
        uint256 amount = (s.stage3EntryStake * appliedBps) / 10_000;

        uint256 slashed = _slashingController().applyComputePenaltySlash(node, amount);
        emit SlashApplied(
            node, epoch, Stage.Stage3Suspended, slashed, triggerType, _recommendationFor(triggerType), 2 /* CRITICAL */
        );
    }

    function _applyStage4OngoingSlash(address node, uint256 epoch, ComputePenaltyState storage s) internal {
        uint256 bps = config().getUint(ConfigKeys.COMPUTE_PENALTY_STAGE4_ONGOING_BPS);
        uint256 amount = (s.stage4Base * bps) / 10_000;
        if (amount == 0) return;
        uint256 slashed = _slashingController().applyComputePenaltySlash(node, amount);
        emit SlashApplied(
            node,
            epoch,
            Stage.Stage4Removed,
            slashed,
            TriggerType.DataLoss,
            "Node remains removed pending re-registration",
            3 /* EMERGENCY */
        );
    }

    // --- Immediate bypasses (issue-external oracle inputs) ---

    /// @notice Stage 3 bypass: "a hosted database becoming unavailable due
    /// to confirmed compute node failure". Fires directly from ANY prior
    /// stage (including a clean Stage 0 node), same-epoch, not deferred.
    /// Permissionless -- correctness is enforced by requiring the
    /// configured `IComputeFailureOracle` to confirm the event, not by
    /// restricting the caller.
    function reportDatabaseUnavailable(address node, uint256 epoch, uint256 hostedDatabaseCount, bytes32 evidenceRef)
        external
        whenNotPaused
    {
        ComputePenaltyStorage storage $ = _storage();
        if ($.sealedEpoch[node][epoch]) revert AlreadySealed(node, epoch);

        IComputeFailureOracle oracle = IComputeFailureOracle(config().getAddress(ConfigKeys.COMPUTE_PENALTY_FAILURE_ORACLE));
        if (!oracle.confirmedNodeFailureUnavailability(node, epoch, evidenceRef)) {
            revert DatabaseUnavailabilityNotConfirmed(node, epoch);
        }

        $.sealedEpoch[node][epoch] = true;
        ComputePenaltyState storage s = $.states[node];
        Stage previousStage = s.stage;
        if (previousStage == Stage.Stage4Removed) {
            // Already at the terminal stage; nothing further to escalate.
            $.epochMultiplierBps[node][epoch] = _stageMultBps(Stage.Stage4Removed);
            return;
        }

        _enterStage3(node, epoch, s, previousStage, TriggerType.DatabaseUnavailable, hostedDatabaseCount);
        $.epochMultiplierBps[node][epoch] = _stageMultBps(s.stage);
    }

    /// @notice Stage 4 trigger: confirmed data loss attributable to `node`.
    /// Immediate 20% slash of the current stake balance plus a continuing
    /// 5%/epoch of the post-slash flat base (applied via `sealEpoch` while
    /// the node remains in Stage 4). Additive to any prior Stage 3
    /// accrued slashing -- `stage3CumulativeSlashedBps` is left untouched,
    /// and this slash is computed off the live stake balance, so there is
    /// no double-counting or overwriting of prior slash history.
    function reportDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external whenNotPaused {
        ComputePenaltyStorage storage $ = _storage();
        if ($.sealedEpoch[node][epoch]) revert AlreadySealed(node, epoch);

        IDataLossOracle oracle = IDataLossOracle(config().getAddress(ConfigKeys.COMPUTE_PENALTY_DATA_LOSS_ORACLE));
        if (!oracle.confirmedDataLoss(node, epoch, evidenceRef)) revert DataLossNotConfirmed(node, epoch);

        $.sealedEpoch[node][epoch] = true;
        ComputePenaltyState storage s = $.states[node];
        Stage previousStage = s.stage;

        uint256 currentStake = _registry().stakeOf(node);
        uint256 immediateBps = config().getUint(ConfigKeys.COMPUTE_PENALTY_STAGE4_IMMEDIATE_BPS);
        uint256 immediateAmount = (currentStake * immediateBps) / 10_000;

        s.stage = Stage.Stage4Removed;
        s.stage4Base = currentStake > immediateAmount ? currentStake - immediateAmount : 0;
        s.cleanEpochStreak = 0;
        s.placementHoldActive = false;
        s.placementHoldCleanEpoch = 0;
        s.lastTriggerType = TriggerType.DataLoss;
        s.lastEvaluatedEpoch = epoch;
        s.everEvaluated = true;
        $.epochMultiplierBps[node][epoch] = _stageMultBps(Stage.Stage4Removed);

        uint256 slashed = _slashingController().applyComputePenaltySlash(node, immediateAmount);
        // Forced removal from the active registry; re-registration resets
        // state to a clean Stage 0 rather than reusing the 3-epoch recovery
        // path.
        _stakingRegistryForceDeregister(node);

        emit SlashApplied(
            node, epoch, Stage.Stage4Removed, slashed, TriggerType.DataLoss, _recommendationFor(TriggerType.DataLoss), 3 /* EMERGENCY */
        );
        emit StageTransition(
            node,
            previousStage,
            Stage.Stage4Removed,
            epoch,
            TriggerType.DataLoss,
            "20% immediate slash applied; node force-deregistered; tenants notified",
            _recommendationFor(TriggerType.DataLoss),
            3 /* EMERGENCY */
        );
        emit TenantDataLossNotification(node, epoch, evidenceRef);
    }

    /// @dev Narrow call shape for `StakingNodeRegistry.forceDeregister`,
    /// declared inline rather than importing the concrete contract so this
    /// file depends only on the one function it calls (mirrors the
    /// `IFoundationWitnessSource` pattern in `ComputeTierRegistry`).
    function _stakingRegistryForceDeregister(address node) internal {
        IForceDeregisterRegistry(config().getAddress(ConfigKeys.REGISTRY_ADDRESS)).forceDeregister(node);
    }

    // --- Shared helpers ---

    function _transitionTo(
        address node,
        uint256 epoch,
        ComputePenaltyState storage s,
        Stage previousStage,
        Stage newStage,
        TriggerType triggerType,
        string memory action,
        string memory recommendation,
        uint8 severity
    ) internal {
        s.stage = newStage;
        emit StageTransition(node, previousStage, newStage, epoch, triggerType, action, recommendation, severity);
    }

    function _emitMigrationSignal(address node, uint256 epoch, MigrationUrgency urgency, uint256 hostedDatabaseCount)
        internal
    {
        if (hostedDatabaseCount == 0) return;
        emit MigrationSignal(node, epoch, urgency, hostedDatabaseCount);
    }

    function _stageMultBps(Stage stage) internal view returns (uint256) {
        return config().getUint(ConfigKeys.computePenaltyStageMultBps(uint8(stage)));
    }

    function _recommendationFor(TriggerType triggerType) internal pure returns (string memory) {
        if (triggerType == TriggerType.Latency) {
            return "Check for CPU/query contention driving elevated P99 latency";
        }
        if (triggerType == TriggerType.RestartRate) {
            return "Investigate recurring Postgres process restarts";
        }
        if (triggerType == TriggerType.WalLag) {
            return "Check WAL safekeeper peer connectivity";
        }
        if (triggerType == TriggerType.MemoryPressure) {
            return "Check for OOM killer invocations; reduce hosted database count to relieve CPU/memory pressure";
        }
        if (triggerType == TriggerType.DatabaseUnavailable) {
            return "Confirm compute node health and hasten hosted-database migration";
        }
        if (triggerType == TriggerType.DataLoss) {
            return "Preserve WAL/safekeeper evidence and notify affected tenants";
        }
        if (triggerType == TriggerType.MissingTelemetry) {
            return "Restore telemetry reporting -- missing epochs are treated as unhealthy";
        }
        return "No action needed";
    }
}

/// @dev See `_stakingRegistryForceDeregister`.
interface IForceDeregisterRegistry {
    function forceDeregister(address node) external;
}
