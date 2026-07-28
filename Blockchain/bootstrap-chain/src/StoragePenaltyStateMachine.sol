// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";
import {ISlashingController} from "./interfaces/ISlashingController.sol";
import {IStoragePenaltyStateMachine} from "./interfaces/IStoragePenaltyStateMachine.sol";
import {IStorageIntegrityOracle} from "./interfaces/IStorageIntegrityOracle.sol";

/// @title StoragePenaltyStateMachine
/// @notice Issue #82: storage-node telemetry-driven progressive penalty
/// state machine (Stage 0 Healthy .. Stage 4 Removed), analogous in shape
/// to the compute-node variant (issue #91) but with its own storage-node
/// trigger set (missed heartbeats, checksum error rate, degraded I/O
/// throughput, failed PoR challenges, confirmed partial/permanent
/// redundancy loss) from `node-runner-incentives-and-penalties.md`'s
/// Penalties and Slashing section. Kept as its own contract rather than
/// folded into `SlashingController`'s existing `submitFaultAttestation`
/// path -- that path decides the applicable stage entirely from an
/// off-chain-signed attestation, which does not satisfy FR-5's requirement
/// that stage-transition triggers be "deterministic ... sourced from
/// #75/#79's data" and "objectively verifiable by any observer" against
/// documented, fixed thresholds. This contract evaluates those thresholds
/// on-chain from submitted telemetry and reuses `SlashingController`'s
/// existing slash-execution/token-disposition plumbing (`applyStoragePenaltySlash`)
/// rather than duplicating it.
///
/// `stagePenaltyMult` is applied multiplicatively to a node's separately
/// computed reward score -- this contract does not compute or touch that
/// score itself.
contract StoragePenaltyStateMachine is GovernedUpgradeable, IStoragePenaltyStateMachine {
    struct StorageTelemetry {
        uint256 heartbeatMisses;
        uint256 checksumErrorRateBps;
        uint256 ioDegradationBps;
        bool porFailed;
        bool porAuditPassed;
        bool reported;
    }

    struct StoragePenaltyState {
        Stage stage;
        uint256 troubleEpochCount;
        uint256 cleanEpochStreak;
        bool dealHoldActive;
        uint8 dealHoldCleanEpoch; // 0/1, per spec's "+1 additional clean epoch" deal-eligibility hold
        // Stage 3 episode tracking (spec AC: the 25% cap resets only on
        // full recovery to Stage 0, not on merely no longer meeting Stage
        // 3's trigger condition).
        uint256 stage3EpisodeId;
        uint256 stage3EntryStake;
        uint256 stage3CumulativeSlashedBps;
        // Stage 4
        uint256 stage4Base; // lockedStake immediately after the 20% immediate slash; frozen for this Stage 4 dwell
        TriggerType lastTriggerType;
        uint256 lastEvaluatedEpoch;
        bool everEvaluated;
    }

    /// @custom:storage-location erc7201:symposia.StoragePenaltyStateMachine
    struct StoragePenaltyStorage {
        mapping(address => StoragePenaltyState) states;
        mapping(address => mapping(uint256 => StorageTelemetry)) telemetry;
        mapping(address => mapping(uint256 => bool)) sealedEpoch;
        mapping(address => mapping(uint256 => uint256)) epochMultiplierBps;
    }

    bytes32 private constant STORAGE_PENALTY_STORAGE_LOCATION = keccak256("symposia.storage.StoragePenaltyStateMachine");

    error NotNode(address caller, address node);
    error AlreadySealed(address node, uint256 epoch);
    error OutOfOrderEpoch(uint256 provided, uint256 expected);
    error PartialDataLossNotConfirmed(address node, uint256 epoch);
    error PermanentDataLossNotConfirmed(address node, uint256 epoch);

    function _storage() private pure returns (StoragePenaltyStorage storage $) {
        bytes32 slot = STORAGE_PENALTY_STORAGE_LOCATION;
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

    function _integrityOracle() internal view returns (IStorageIntegrityOracle) {
        return IStorageIntegrityOracle(config().getAddress(ConfigKeys.STORAGE_PENALTY_INTEGRITY_ORACLE));
    }

    // --- Views ---

    function stateOf(address node) external view returns (StoragePenaltyState memory) {
        return _storage().states[node];
    }

    function currentStageOf(address node) external view returns (Stage) {
        return _storage().states[node].stage;
    }

    function telemetryOf(address node, uint256 epoch) external view returns (StorageTelemetry memory) {
        return _storage().telemetry[node][epoch];
    }

    function isSealed(address node, uint256 epoch) external view returns (bool) {
        return _storage().sealedEpoch[node][epoch];
    }

    /// @notice Reward multiplier (bps out of 10_000) locked in at the
    /// moment `epoch` was sealed for `node`. Immutable once sealed; returns
    /// 0 for an epoch that has not yet been sealed (same "unset == 0"
    /// convention `IProtocolConfig` uses).
    function stagePenaltyMult(address node, uint256 epoch) external view returns (uint256) {
        return _storage().epochMultiplierBps[node][epoch];
    }

    /// @notice A node is re-eligible for new storage deals only after 1
    /// additional clean epoch beyond the 3-epoch recovery (FR-8/AC): while
    /// in any active penalty stage it is never eligible; immediately upon
    /// exiting to Stage 0 it still needs one more clean epoch before the
    /// hold clears.
    function dealEligible(address node) external view returns (bool) {
        StoragePenaltyState storage s = _storage().states[node];
        if (s.stage != Stage.Stage0Healthy) return false;
        if (!s.dealHoldActive) return true;
        return s.dealHoldCleanEpoch == 1;
    }

    // --- Telemetry submission (self-reported; #75/#79's aggregated feeds are out of scope) ---

    function submitTelemetry(
        address node,
        uint256 epoch,
        uint256 heartbeatMisses,
        uint256 checksumErrorRateBps,
        uint256 ioDegradationBps,
        bool porFailed,
        bool porAuditPassed
    ) external whenNotPaused {
        if (msg.sender != node) revert NotNode(msg.sender, node);
        if (_storage().sealedEpoch[node][epoch]) revert AlreadySealed(node, epoch);

        _storage().telemetry[node][epoch] = StorageTelemetry({
            heartbeatMisses: heartbeatMisses,
            checksumErrorRateBps: checksumErrorRateBps,
            ioDegradationBps: ioDegradationBps,
            porFailed: porFailed,
            porAuditPassed: porAuditPassed,
            reported: true
        });
    }

    // --- Per-epoch evaluation / seal (telemetry-driven path) ---

    /// @notice Permissionless keeper entrypoint: evaluates `node`'s Stage
    /// 1/2/3-via-persistence triggers and recovery progress for `epoch`
    /// from whatever telemetry (if any) was submitted, then seals the
    /// epoch. Sealing is a structural idempotency guard -- effects (state
    /// writes, the `sealedEpoch` flag itself) are applied before any
    /// external call (checks-effects-interactions), so a reentrant or
    /// duplicate call cannot double-slash.
    function sealEpoch(address node, uint256 epoch) external whenNotPaused {
        StoragePenaltyStorage storage $ = _storage();
        if ($.sealedEpoch[node][epoch]) revert AlreadySealed(node, epoch);
        $.sealedEpoch[node][epoch] = true;

        StoragePenaltyState storage s = $.states[node];

        if (s.stage == Stage.Stage4Removed) {
            // A node that has completed re-registration since being
            // force-deregistered is no longer `Banned` at the registry --
            // treat that as the fresh-Stage-0 start a real re-registration
            // implies (no prior stage3/stage4 history carried over), rather
            // than continuing to apply ongoing Stage 4 slashing against a
            // node that has already re-onboarded.
            if (_registry().statusOf(node) != INodeRegistry.NodeStatus.Banned) {
                delete $.states[node];
                $.epochMultiplierBps[node][epoch] = _stageMultBps(Stage.Stage0Healthy);
                StoragePenaltyState storage fresh = $.states[node];
                fresh.lastEvaluatedEpoch = epoch;
                fresh.everEvaluated = true;
                return;
            }
            // Continuing 5%/epoch of the post-20%-slash flat base while the
            // node remains in Stage 4; not gated on telemetry ordering
            // since a Stage 4 node is expected to stop reporting.
            _applyStage4OngoingSlash(node, epoch, s);
            $.epochMultiplierBps[node][epoch] = _stageMultBps(Stage.Stage4Removed);
            s.lastEvaluatedEpoch = epoch;
            return;
        }

        Stage previousStage = s.stage;
        uint256 expected = s.everEvaluated ? s.lastEvaluatedEpoch + 1 : epoch;
        if (epoch != expected) revert OutOfOrderEpoch(epoch, expected);

        StorageTelemetry storage t = $.telemetry[node][epoch];
        if (!t.reported) {
            // A gap in reporting must not read as "healthy" and must not
            // count toward the clean-epoch recovery streak -- treated the
            // same as a failing epoch, with its own trigger type so alert
            // payloads can distinguish "node didn't report" from "node
            // reported bad numbers".
            _handleUnhealthyEpoch(node, epoch, s, previousStage, TriggerType.MissingTelemetry);
        } else {
            (bool heartbeatTrig, bool checksumTrig, bool ioTrig, bool porTrig, bool spikeTrig) =
                _evaluateTriggers(t);
            if (spikeTrig) {
                _bypassToStage2(node, epoch, s, previousStage, TriggerType.ChecksumErrorRate);
            } else if (heartbeatTrig || checksumTrig || ioTrig || porTrig) {
                TriggerType triggerType = heartbeatTrig
                    ? TriggerType.Heartbeat
                    : (checksumTrig ? TriggerType.ChecksumErrorRate : (ioTrig ? TriggerType.IoDegradation : TriggerType.PorFailure));
                _handleUnhealthyEpoch(node, epoch, s, previousStage, triggerType);
            } else {
                _handleCleanEpoch(node, epoch, s, t.porAuditPassed);
            }
        }

        s.lastEvaluatedEpoch = epoch;
        s.everEvaluated = true;
        $.epochMultiplierBps[node][epoch] = _stageMultBps(s.stage);
    }

    // --- Trigger evaluation layer ---

    function _evaluateTriggers(StorageTelemetry storage t)
        internal
        view
        returns (bool heartbeatTrig, bool checksumTrig, bool ioTrig, bool porTrig, bool spikeTrig)
    {
        uint256 heartbeatThreshold = config().getUint(ConfigKeys.STORAGE_PENALTY_HEARTBEAT_MISS_THRESHOLD);
        heartbeatTrig = t.heartbeatMisses > heartbeatThreshold;

        uint256 checksumThreshold = config().getUint(ConfigKeys.STORAGE_PENALTY_CHECKSUM_ERROR_RATE_BPS);
        checksumTrig = t.checksumErrorRateBps > checksumThreshold;

        uint256 ioThreshold = config().getUint(ConfigKeys.STORAGE_PENALTY_IO_DEGRADATION_BPS);
        ioTrig = t.ioDegradationBps > ioThreshold;

        porTrig = t.porFailed;

        uint256 spikeThreshold = config().getUint(ConfigKeys.STORAGE_PENALTY_CHECKSUM_SPIKE_BPS);
        spikeTrig = t.checksumErrorRateBps > spikeThreshold;
    }

    // --- Stage transition table ---

    function _handleUnhealthyEpoch(
        address node,
        uint256 epoch,
        StoragePenaltyState storage s,
        Stage previousStage,
        TriggerType triggerType
    ) internal {
        s.cleanEpochStreak = 0;
        s.lastTriggerType = triggerType;
        s.troubleEpochCount += 1;
        // Note: dealHoldActive/dealHoldCleanEpoch are deliberately NOT
        // reset here for a Stage0Healthy node that hasn't yet escalated
        // back into Stage 1 -- a single failing epoch during the post-exit
        // deal-eligibility hold must further delay eligibility (stay
        // ineligible), not flip it to "no hold in effect" eligible. They
        // are reset to false/0 explicitly inside the escalation branches
        // below, where a real stage transition actually occurs.

        uint256 stage1At = config().getUint(ConfigKeys.STORAGE_PENALTY_STAGE1_TROUBLE_EPOCHS);
        uint256 stage2At = config().getUint(ConfigKeys.STORAGE_PENALTY_STAGE2_TROUBLE_EPOCHS);
        uint256 stage3At = config().getUint(ConfigKeys.STORAGE_PENALTY_STAGE3_TROUBLE_EPOCHS);

        if (s.stage == Stage.Stage0Healthy) {
            if (s.troubleEpochCount >= stage1At) {
                s.dealHoldActive = false;
                s.dealHoldCleanEpoch = 0;
                _transitionTo(
                    node,
                    epoch,
                    s,
                    previousStage,
                    Stage.Stage1Warning,
                    triggerType,
                    "70% reward multiplier applied; precautionary re-replication started",
                    _recommendationFor(triggerType),
                    0 /* INFO */
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
                    "40% reward multiplier applied; accelerated re-replication",
                    _recommendationFor(triggerType),
                    1 /* WARNING */
                );
            }
        } else if (s.stage == Stage.Stage2Degraded) {
            if (s.troubleEpochCount >= stage3At) {
                _enterStage3(node, epoch, s, previousStage, triggerType);
            }
        } else if (s.stage == Stage.Stage3Suspended) {
            _applyStage3Slash(node, epoch, s, triggerType);
        }
        // Stage4Removed is handled entirely by the early-return branch in
        // `sealEpoch` and never reaches this function.
    }

    function _handleCleanEpoch(address node, uint256 epoch, StoragePenaltyState storage s, bool auditPassed)
        internal
    {
        s.lastTriggerType = TriggerType.Recovery;
        // A clean epoch always resets the consecutive-trouble counter,
        // regardless of current stage -- persistence-based escalation
        // (Stage 1 -> 2 -> 3) must be enforced against strictly
        // *consecutive* troubled epochs, not a rolling/cumulative count
        // that survives an intervening clean epoch. The node's `stage`
        // itself does not drop back down on a single clean epoch -- only
        // the full 3-consecutive-clean-epoch + PoR-audit recovery path
        // below exits an active stage.
        s.troubleEpochCount = 0;

        if (s.stage == Stage.Stage0Healthy) {
            if (s.dealHoldActive && s.dealHoldCleanEpoch == 0) {
                s.dealHoldCleanEpoch = 1;
                s.dealHoldActive = false;
            }
            return;
        }

        s.cleanEpochStreak += 1;
        uint256 required = config().getUint(ConfigKeys.STORAGE_PENALTY_RECOVERY_CLEAN_EPOCHS);
        if (s.cleanEpochStreak >= required && auditPassed) {
            Stage previousStage = s.stage;
            s.stage = Stage.Stage0Healthy;
            s.troubleEpochCount = 0;
            s.cleanEpochStreak = 0;
            s.dealHoldActive = true;
            s.dealHoldCleanEpoch = 0;
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
        // If the clean-epoch streak has reached `required` but the audit
        // hasn't passed yet, recovery does not trigger this epoch -- the
        // streak is left intact (not reset) so a later epoch's passing
        // audit can still complete recovery without restarting the count.
    }

    function _bypassToStage2(
        address node,
        uint256 epoch,
        StoragePenaltyState storage s,
        Stage previousStage,
        TriggerType triggerType
    ) internal {
        s.cleanEpochStreak = 0;
        s.dealHoldActive = false;
        s.dealHoldCleanEpoch = 0;
        s.lastTriggerType = triggerType;

        if (s.stage == Stage.Stage0Healthy || s.stage == Stage.Stage1Warning) {
            uint256 stage2At = config().getUint(ConfigKeys.STORAGE_PENALTY_STAGE2_TROUBLE_EPOCHS);
            s.troubleEpochCount = stage2At;
            _transitionTo(
                node,
                epoch,
                s,
                previousStage,
                Stage.Stage2Degraded,
                triggerType,
                "40% reward multiplier applied; accelerated re-replication",
                _recommendationFor(triggerType),
                1 /* WARNING */
            );
        }
        // Already Stage2+ -- the spike is noted (lastTriggerType) but does
        // not downgrade an already-more-severe stage.
    }

    function _enterStage3(
        address node,
        uint256 epoch,
        StoragePenaltyState storage s,
        Stage previousStage,
        TriggerType triggerType
    ) internal {
        bool freshEntry = s.stage != Stage.Stage3Suspended;
        if (freshEntry) {
            s.stage3EpisodeId += 1;
            s.stage3EntryStake = _registry().stakeOf(node);
            s.stage3CumulativeSlashedBps = 0;
        }

        uint256 stage3At = config().getUint(ConfigKeys.STORAGE_PENALTY_STAGE3_TROUBLE_EPOCHS);
        s.troubleEpochCount = stage3At;
        s.cleanEpochStreak = 0;
        s.dealHoldActive = false;
        s.dealHoldCleanEpoch = 0;
        s.lastTriggerType = triggerType;

        _transitionTo(
            node,
            epoch,
            s,
            previousStage,
            Stage.Stage3Suspended,
            triggerType,
            "0% reward multiplier; node suspended from new storage deals; blobs actively migrated off",
            _recommendationFor(triggerType),
            2 /* CRITICAL */
        );

        if (freshEntry) {
            _applyStage3Slash(node, epoch, s, triggerType);
        }
    }

    function _applyStage3Slash(address node, uint256 epoch, StoragePenaltyState storage s, TriggerType triggerType)
        internal
    {
        uint256 pctBps = config().getUint(ConfigKeys.STORAGE_PENALTY_STAGE3_PCT_PER_EPOCH_BPS);
        uint256 capBps = config().getUint(ConfigKeys.STORAGE_PENALTY_STAGE3_CAP_BPS);
        uint256 remainingCap = capBps > s.stage3CumulativeSlashedBps ? capBps - s.stage3CumulativeSlashedBps : 0;
        uint256 appliedBps = pctBps > remainingCap ? remainingCap : pctBps;
        if (appliedBps == 0) return;

        s.stage3CumulativeSlashedBps += appliedBps;
        uint256 amount = (s.stage3EntryStake * appliedBps) / 10_000;

        uint256 slashed = _slashingController().applyStoragePenaltySlash(node, amount);
        emit SlashApplied(
            node, epoch, Stage.Stage3Suspended, slashed, triggerType, _recommendationFor(triggerType), 2 /* CRITICAL */
        );
    }

    function _applyStage4OngoingSlash(address node, uint256 epoch, StoragePenaltyState storage s) internal {
        uint256 bps = config().getUint(ConfigKeys.STORAGE_PENALTY_STAGE4_ONGOING_BPS);
        uint256 amount = (s.stage4Base * bps) / 10_000;
        if (amount == 0) return;
        uint256 slashed = _slashingController().applyStoragePenaltySlash(node, amount);
        emit SlashApplied(
            node,
            epoch,
            Stage.Stage4Removed,
            slashed,
            TriggerType.PermanentDataLoss,
            "Node remains removed pending re-registration",
            3 /* EMERGENCY */
        );
    }

    // --- Immediate bypasses (issue-external oracle inputs) ---

    /// @notice Stage 3 bypass: confirmed partial data loss (redundancy for
    /// affected blob(s) dropped below the minimum replication factor).
    /// Fires directly from ANY prior stage (including a clean Stage 0
    /// node), same-epoch, not deferred. Permissionless -- correctness is
    /// enforced by requiring the configured `IStorageIntegrityOracle` to
    /// confirm the event, not by restricting the caller.
    function reportPartialDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external whenNotPaused {
        StoragePenaltyStorage storage $ = _storage();
        if ($.sealedEpoch[node][epoch]) revert AlreadySealed(node, epoch);

        if (!_integrityOracle().confirmedPartialDataLoss(node, epoch, evidenceRef)) {
            revert PartialDataLossNotConfirmed(node, epoch);
        }

        $.sealedEpoch[node][epoch] = true;
        StoragePenaltyState storage s = $.states[node];
        Stage previousStage = s.stage;
        if (previousStage == Stage.Stage4Removed) {
            // Already at the terminal stage; nothing further to escalate.
            $.epochMultiplierBps[node][epoch] = _stageMultBps(Stage.Stage4Removed);
            return;
        }

        _enterStage3(node, epoch, s, previousStage, TriggerType.PartialDataLoss);
        $.epochMultiplierBps[node][epoch] = _stageMultBps(s.stage);
    }

    /// @notice Stage 4 trigger: confirmed permanent, unrecoverable loss of
    /// blobs attributable to `node`. Immediate 20% slash of the current
    /// stake balance plus a continuing 5%/epoch of the post-slash flat base
    /// (applied via `sealEpoch` while the node remains in Stage 4).
    /// Additive to any prior Stage 3 accrued slashing --
    /// `stage3CumulativeSlashedBps` is left untouched, and this slash is
    /// computed off the live stake balance, so there is no double-counting
    /// or overwriting of prior slash history.
    function reportPermanentDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external whenNotPaused {
        StoragePenaltyStorage storage $ = _storage();
        if ($.sealedEpoch[node][epoch]) revert AlreadySealed(node, epoch);

        if (!_integrityOracle().confirmedPermanentDataLoss(node, epoch, evidenceRef)) {
            revert PermanentDataLossNotConfirmed(node, epoch);
        }

        $.sealedEpoch[node][epoch] = true;
        StoragePenaltyState storage s = $.states[node];
        Stage previousStage = s.stage;

        uint256 currentStake = _registry().stakeOf(node);
        uint256 immediateBps = config().getUint(ConfigKeys.STORAGE_PENALTY_STAGE4_IMMEDIATE_BPS);
        uint256 immediateAmount = (currentStake * immediateBps) / 10_000;

        s.stage = Stage.Stage4Removed;
        s.stage4Base = currentStake > immediateAmount ? currentStake - immediateAmount : 0;
        s.cleanEpochStreak = 0;
        s.dealHoldActive = false;
        s.dealHoldCleanEpoch = 0;
        s.lastTriggerType = TriggerType.PermanentDataLoss;
        s.lastEvaluatedEpoch = epoch;
        s.everEvaluated = true;
        $.epochMultiplierBps[node][epoch] = _stageMultBps(Stage.Stage4Removed);

        uint256 slashed = _slashingController().applyStoragePenaltySlash(node, immediateAmount);
        // Forced removal from the active registry; re-registration resets
        // state to a clean Stage 0 rather than reusing the 3-epoch
        // recovery path (see the reset branch in `sealEpoch`).
        _slashingController().forceDeregisterStorageNode(node);

        emit SlashApplied(
            node,
            epoch,
            Stage.Stage4Removed,
            slashed,
            TriggerType.PermanentDataLoss,
            _recommendationFor(TriggerType.PermanentDataLoss),
            3 /* EMERGENCY */
        );
        emit StageTransition(
            node,
            previousStage,
            Stage.Stage4Removed,
            epoch,
            TriggerType.PermanentDataLoss,
            "20% immediate slash applied; node force-deregistered; tenants notified",
            _recommendationFor(TriggerType.PermanentDataLoss),
            3 /* EMERGENCY */
        );
        emit TenantDataLossNotification(node, epoch, evidenceRef);
    }

    // --- Shared helpers ---

    function _transitionTo(
        address node,
        uint256 epoch,
        StoragePenaltyState storage s,
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

    function _stageMultBps(Stage stage) internal view returns (uint256) {
        return config().getUint(ConfigKeys.storagePenaltyStageMultBps(uint8(stage)));
    }

    function _recommendationFor(TriggerType triggerType) internal pure returns (string memory) {
        if (triggerType == TriggerType.Heartbeat) {
            return "Check node connectivity and process health for missed heartbeats";
        }
        if (triggerType == TriggerType.ChecksumErrorRate) {
            return "Run a S.M.A.R.T. check / filesystem check on the affected volume";
        }
        if (triggerType == TriggerType.IoDegradation) {
            return "Investigate degraded I/O throughput; consider voluntary capacity reduction";
        }
        if (triggerType == TriggerType.PorFailure) {
            return "Investigate failed proof-of-possession challenge; verify blob availability";
        }
        if (triggerType == TriggerType.PartialDataLoss) {
            return "Preserve replication evidence; blobs are being migrated to restore minimum redundancy";
        }
        if (triggerType == TriggerType.PermanentDataLoss) {
            return "Preserve integrity-audit evidence and notify affected tenants";
        }
        if (triggerType == TriggerType.MissingTelemetry) {
            return "Restore telemetry reporting -- missing epochs are treated as unhealthy";
        }
        return "No action needed";
    }
}
