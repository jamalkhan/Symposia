// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {StoragePenaltyStateMachine} from "../src/StoragePenaltyStateMachine.sol";
import {IStoragePenaltyStateMachine} from "../src/interfaces/IStoragePenaltyStateMachine.sol";
import {MockStorageIntegrityOracle} from "./mocks/MockStorageIntegrityOracle.sol";

contract StoragePenaltyStateMachineTest is CoreProtocolTestBase {
    StoragePenaltyStateMachine internal penalty;
    MockStorageIntegrityOracle internal integrityOracle;

    address internal node = makeAddr("storageNode");

    uint256 internal constant HEARTBEAT_THRESHOLD = 2;
    uint256 internal constant CHECKSUM_THRESHOLD_BPS = 100; // 1%
    uint256 internal constant IO_DEGRADATION_THRESHOLD_BPS = 2_000; // 20%
    uint256 internal constant CHECKSUM_SPIKE_BPS = 1_000; // 10%

    function setUp() public {
        _deployCoreProtocol();

        penalty = StoragePenaltyStateMachine(
            _deployProxy(
                address(new StoragePenaltyStateMachine()), abi.encodeCall(StoragePenaltyStateMachine.initialize, (cfg))
            )
        );
        integrityOracle = new MockStorageIntegrityOracle();

        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.STORAGE_PENALTY_STATE_MACHINE_ADDRESS, address(penalty));
        cfg.setAddress(ConfigKeys.STORAGE_PENALTY_INTEGRITY_ORACLE, address(integrityOracle));

        cfg.setUint(ConfigKeys.STORAGE_PENALTY_HEARTBEAT_MISS_THRESHOLD, HEARTBEAT_THRESHOLD);
        cfg.setUint(ConfigKeys.STORAGE_PENALTY_CHECKSUM_ERROR_RATE_BPS, CHECKSUM_THRESHOLD_BPS);
        cfg.setUint(ConfigKeys.STORAGE_PENALTY_IO_DEGRADATION_BPS, IO_DEGRADATION_THRESHOLD_BPS);
        cfg.setUint(ConfigKeys.STORAGE_PENALTY_CHECKSUM_SPIKE_BPS, CHECKSUM_SPIKE_BPS);

        cfg.setUint(ConfigKeys.STORAGE_PENALTY_STAGE1_TROUBLE_EPOCHS, 1);
        cfg.setUint(ConfigKeys.STORAGE_PENALTY_STAGE2_TROUBLE_EPOCHS, 2);
        cfg.setUint(ConfigKeys.STORAGE_PENALTY_STAGE3_TROUBLE_EPOCHS, 4);
        cfg.setUint(ConfigKeys.STORAGE_PENALTY_RECOVERY_CLEAN_EPOCHS, 3);

        cfg.setUint(ConfigKeys.STORAGE_PENALTY_STAGE3_PCT_PER_EPOCH_BPS, 500);
        cfg.setUint(ConfigKeys.STORAGE_PENALTY_STAGE3_CAP_BPS, 2_500);
        cfg.setUint(ConfigKeys.STORAGE_PENALTY_STAGE4_IMMEDIATE_BPS, 2_000);
        cfg.setUint(ConfigKeys.STORAGE_PENALTY_STAGE4_ONGOING_BPS, 500);

        cfg.setUint(ConfigKeys.storagePenaltyStageMultBps(0), 10_000);
        cfg.setUint(ConfigKeys.storagePenaltyStageMultBps(1), 7_000);
        cfg.setUint(ConfigKeys.storagePenaltyStageMultBps(2), 4_000);
        cfg.setUint(ConfigKeys.storagePenaltyStageMultBps(3), 0);
        cfg.setUint(ConfigKeys.storagePenaltyStageMultBps(4), 0);
        vm.stopPrank();

        _fund(node, 10_000e18);
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 1);
    }

    // --- Telemetry helpers ---

    function _submit(
        address who,
        uint256 epoch,
        uint256 heartbeatMisses,
        uint256 checksumBps,
        uint256 ioBps,
        bool porFailed,
        bool auditPassed
    ) internal {
        vm.prank(who);
        penalty.submitTelemetry(who, epoch, heartbeatMisses, checksumBps, ioBps, porFailed, auditPassed);
    }

    function _submitClean(address who, uint256 epoch) internal {
        _submit(who, epoch, 0, 0, 0, false, false);
    }

    function _submitCleanWithAudit(address who, uint256 epoch) internal {
        _submit(who, epoch, 0, 0, 0, false, true);
    }

    function _submitHeartbeatTrigger(address who, uint256 epoch) internal {
        _submit(who, epoch, HEARTBEAT_THRESHOLD + 1, 0, 0, false, false);
    }

    function _submitChecksumTrigger(address who, uint256 epoch) internal {
        _submit(who, epoch, 0, CHECKSUM_THRESHOLD_BPS + 1, 0, false, false);
    }

    function _submitIoTrigger(address who, uint256 epoch) internal {
        _submit(who, epoch, 0, 0, IO_DEGRADATION_THRESHOLD_BPS + 1, false, false);
    }

    function _submitPorFailure(address who, uint256 epoch) internal {
        _submit(who, epoch, 0, 0, 0, true, false);
    }

    function _submitChecksumSpike(address who, uint256 epoch) internal {
        _submit(who, epoch, 0, CHECKSUM_SPIKE_BPS + 1, 0, false, false);
    }

    function _driveToStage2(address who) internal {
        _submitHeartbeatTrigger(who, 1);
        penalty.sealEpoch(who, 1);
        _submitHeartbeatTrigger(who, 2);
        penalty.sealEpoch(who, 2);
    }

    // --- Stage 1 entry ---

    function test_stage1_entersOnFirstTroubledEpoch() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));
        assertEq(penalty.stagePenaltyMult(node, 1), 7_000);
        assertEq(registry.stakeOf(node), registry.minStakeFor(INodeRegistry.NodeType.Storage, 1));
    }

    function test_stage1_eachTriggerIndependently() public {
        _submitChecksumTrigger(node, 1);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));
    }

    function test_stage1_ioTriggerIndependently() public {
        _submitIoTrigger(node, 1);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));
    }

    function test_stage1_porFailureTriggerIndependently() public {
        _submitPorFailure(node, 1);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));
    }

    function test_stage1_belowThreshold_doesNotTrigger() public {
        _submit(node, 1, HEARTBEAT_THRESHOLD, 0, 0, false, false); // exactly at threshold, not above
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage0Healthy));
    }

    // --- Stage 2 via persistence and via checksum-spike bypass ---

    function test_stage2_viaPersistence_twoConsecutiveTroubledEpochs() public {
        _driveToStage2(node);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage2Degraded));
        assertEq(penalty.stagePenaltyMult(node, 2), 4_000);
    }

    function test_stage2_doesNotTriggerOnNonConsecutiveTrouble() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);
        _submitClean(node, 2);
        penalty.sealEpoch(node, 2);
        _submitHeartbeatTrigger(node, 3);
        penalty.sealEpoch(node, 3);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));
    }

    function test_stage2_viaChecksumSpikeBypass_fromCleanNode() public {
        _submitChecksumSpike(node, 1);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage2Degraded));
    }

    // --- Stage 3 via persistence and via partial-data-loss bypass ---

    function test_stage3_viaPersistence_fourthTroubledEpoch() public {
        _driveToStage2(node);
        _submitHeartbeatTrigger(node, 3);
        penalty.sealEpoch(node, 3);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage2Degraded));

        _submitHeartbeatTrigger(node, 4);
        penalty.sealEpoch(node, 4);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage3Suspended));
        assertEq(penalty.stagePenaltyMult(node, 4), 0);
    }

    function test_stage3_viaPartialDataLossBypass_fromStage0() public {
        bytes32 evidence = keccak256("partial-loss-1");
        integrityOracle.setPartialConfirmed(node, 1, evidence, true);
        penalty.reportPartialDataLoss(node, 1, evidence);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage3Suspended));
    }

    function test_stage3_viaPartialDataLossBypass_fromStage1_supersedes() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));

        bytes32 evidence = keccak256("partial-loss-2");
        integrityOracle.setPartialConfirmed(node, 2, evidence, true);
        penalty.reportPartialDataLoss(node, 2, evidence);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage3Suspended));
    }

    // --- Stage 3 cumulative slash cap (golden-value arithmetic) ---

    function test_stage3_cumulativeSlashCapsAt25Percent() public {
        _driveToStage2(node);
        _submitHeartbeatTrigger(node, 3);
        penalty.sealEpoch(node, 3);
        uint256 entryStake = registry.stakeOf(node);

        _submitHeartbeatTrigger(node, 4); // stage3 entry epoch, first 5% slash
        penalty.sealEpoch(node, 4);

        for (uint256 e = 5; e <= 10; e++) {
            _submitHeartbeatTrigger(node, e);
            penalty.sealEpoch(node, e);
        }

        uint256 expectedSlashed = (entryStake * 2_500) / 10_000; // 25% cap, exact
        assertEq(entryStake - registry.stakeOf(node), expectedSlashed);
    }

    function test_stage3_capResetsOnlyAfterFullRecovery_notMereExit() public {
        // Drive into Stage 3, accrue some slash, then improve to a couple of
        // clean epochs (NOT a full 3-epoch + audit recovery), then relapse
        // into Stage 3 trouble again -- the 25% cap must still track the
        // *same* episode/entry-stake, not reset to a fresh allowance.
        uint256 entryStake = registry.stakeOf(node);
        bytes32 evidence1 = keccak256("partial-loss-3");
        integrityOracle.setPartialConfirmed(node, 1, evidence1, true);
        penalty.reportPartialDataLoss(node, 1, evidence1); // fresh episode, 5% of entryStake slashed

        _submitClean(node, 2); // improves, but recovery streak only at 1 -- still Stage 3
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage3Suspended));
        _submitClean(node, 3); // streak at 2
        penalty.sealEpoch(node, 3);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage3Suspended));

        // Relapses before completing recovery -- same episode continues.
        for (uint256 e = 4; e <= 12; e++) {
            _submitHeartbeatTrigger(node, e);
            penalty.sealEpoch(node, e);
        }

        uint256 expectedSlashed = (entryStake * 2_500) / 10_000; // 25% of the ORIGINAL entry stake
        assertEq(entryStake - registry.stakeOf(node), expectedSlashed);
    }

    function test_stage3_blocksNewStorageDeals() public {
        _driveToStage2(node);
        _submitHeartbeatTrigger(node, 3);
        penalty.sealEpoch(node, 3);
        _submitHeartbeatTrigger(node, 4);
        penalty.sealEpoch(node, 4);

        assertFalse(penalty.dealEligible(node));
    }

    // --- Stage 4 permanent data loss ---

    function test_stage4_immediateTwentyPercentPlusOngoingFivePercent() public {
        uint256 before = registry.stakeOf(node);
        bytes32 evidence = keccak256("permanent-loss-1");
        integrityOracle.setPermanentConfirmed(node, 1, evidence, true);
        penalty.reportPermanentDataLoss(node, 1, evidence);

        uint256 afterImmediate = before - (before * 2_000) / 10_000;
        assertEq(registry.stakeOf(node), afterImmediate);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage4Removed));

        penalty.sealEpoch(node, 2); // ongoing 5% of post-slash flat base
        uint256 afterOngoing = afterImmediate - (afterImmediate * 500) / 10_000;
        assertEq(registry.stakeOf(node), afterOngoing);
    }

    function test_stage4_additiveToStage3History_noDoubleCounting() public {
        _driveToStage2(node);
        _submitHeartbeatTrigger(node, 3);
        penalty.sealEpoch(node, 3);
        _submitHeartbeatTrigger(node, 4); // stage3 entry, first 5% slash
        penalty.sealEpoch(node, 4);
        uint256 afterStage3 = registry.stakeOf(node);

        bytes32 evidence = keccak256("permanent-loss-2");
        integrityOracle.setPermanentConfirmed(node, 5, evidence, true);
        penalty.reportPermanentDataLoss(node, 5, evidence);

        uint256 expectedImmediate = (afterStage3 * 2_000) / 10_000;
        assertEq(registry.stakeOf(node), afterStage3 - expectedImmediate);
    }

    function test_stage4_forcesDeregistration_blocksRegistryGatedActions() public {
        bytes32 evidence = keccak256("permanent-loss-3");
        integrityOracle.setPermanentConfirmed(node, 1, evidence, true);
        penalty.reportPermanentDataLoss(node, 1, evidence);

        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Banned));

        vm.prank(verifierRole);
        vm.expectRevert();
        registry.recordVerification(node, true, 2);
    }

    function test_stage4_reRegistration_startsCleanAtStage0() public {
        bytes32 evidence = keccak256("permanent-loss-4");
        integrityOracle.setPermanentConfirmed(node, 1, evidence, true);
        penalty.reportPermanentDataLoss(node, 1, evidence);

        // banExpiry == block.timestamp, so re-registration is immediately allowed.
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 2);

        penalty.sealEpoch(node, 2); // no longer Banned -> resets to a fresh Stage 0 state
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage0Healthy));
        assertEq(penalty.stagePenaltyMult(node, 2), 10_000);
    }

    // --- Recovery ---

    function test_recovery_afterThreeConsecutiveCleanEpochsWithAudit_restoresFullMultiplier() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));

        uint256 stakeBefore = registry.stakeOf(node);

        _submitClean(node, 2);
        penalty.sealEpoch(node, 2);
        _submitClean(node, 3);
        penalty.sealEpoch(node, 3);
        _submitCleanWithAudit(node, 4);
        penalty.sealEpoch(node, 4);

        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage0Healthy));
        assertEq(penalty.stagePenaltyMult(node, 4), 10_000);
        assertEq(registry.stakeOf(node), stakeBefore); // no stake returned; Stage1 never slashed anything
    }

    function test_recovery_doesNotTriggerWithoutPassingAudit() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);

        _submitClean(node, 2);
        penalty.sealEpoch(node, 2);
        _submitClean(node, 3);
        penalty.sealEpoch(node, 3);
        _submitClean(node, 4); // 3rd consecutive clean epoch, but audit NOT passed
        penalty.sealEpoch(node, 4);

        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));

        // Audit lags -- passes on the following epoch while streak is preserved.
        _submitCleanWithAudit(node, 5);
        penalty.sealEpoch(node, 5);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage0Healthy));
    }

    function test_recovery_clockResetsOnRelapse() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);

        _submitClean(node, 2);
        penalty.sealEpoch(node, 2);
        _submitClean(node, 3);
        penalty.sealEpoch(node, 3);
        // relapse before reaching 3 consecutive clean epochs
        _submitHeartbeatTrigger(node, 4);
        penalty.sealEpoch(node, 4);

        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));

        _submitClean(node, 5);
        penalty.sealEpoch(node, 5);
        _submitClean(node, 6);
        penalty.sealEpoch(node, 6);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));
        _submitCleanWithAudit(node, 7);
        penalty.sealEpoch(node, 7);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage0Healthy));
    }

    // --- Deal eligibility hold ---

    function test_dealEligibility_delayedOneEpochBeyondStageExit() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);

        _submitClean(node, 2);
        penalty.sealEpoch(node, 2);
        _submitClean(node, 3);
        penalty.sealEpoch(node, 3);
        _submitCleanWithAudit(node, 4);
        penalty.sealEpoch(node, 4);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage0Healthy));
        assertFalse(penalty.dealEligible(node)); // exit epoch itself not yet eligible

        _submitClean(node, 5);
        penalty.sealEpoch(node, 5);
        assertTrue(penalty.dealEligible(node));
    }

    function test_dealEligibility_furtherDelayedOnRelapseInHoldEpoch() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);
        _submitClean(node, 2);
        penalty.sealEpoch(node, 2);
        _submitClean(node, 3);
        penalty.sealEpoch(node, 3);
        _submitCleanWithAudit(node, 4);
        penalty.sealEpoch(node, 4);
        assertFalse(penalty.dealEligible(node));

        // A failing epoch during the hold window immediately re-escalates
        // to Stage 1 (Stage 1's own trigger threshold is "first troubled
        // epoch"), which both clears the hold bookkeeping and requires the
        // full 3-consecutive-clean-epoch + audit recovery sequence again --
        // not just the single extra hold epoch -- before deal eligibility
        // returns.
        _submitHeartbeatTrigger(node, 5);
        penalty.sealEpoch(node, 5);
        assertFalse(penalty.dealEligible(node));
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));

        _submitClean(node, 6);
        penalty.sealEpoch(node, 6);
        _submitClean(node, 7);
        penalty.sealEpoch(node, 7);
        assertFalse(penalty.dealEligible(node));
        _submitCleanWithAudit(node, 8);
        penalty.sealEpoch(node, 8);
        assertFalse(penalty.dealEligible(node)); // exit epoch itself still not yet eligible

        _submitClean(node, 9);
        penalty.sealEpoch(node, 9);
        assertTrue(penalty.dealEligible(node));
    }

    // --- Idempotency ---

    function test_idempotent_reEvaluationOfSealedEpoch_reverts() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);

        vm.expectRevert(abi.encodeWithSelector(StoragePenaltyStateMachine.AlreadySealed.selector, node, uint256(1)));
        penalty.sealEpoch(node, 1);
    }

    function test_missingTelemetry_doesNotCountAsCleanOrTowardRecovery() public {
        _submitHeartbeatTrigger(node, 1);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));

        _submitClean(node, 2);
        penalty.sealEpoch(node, 2);
        // epoch 3: no telemetry submitted at all
        penalty.sealEpoch(node, 3);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IStoragePenaltyStateMachine.Stage.Stage1Warning));
    }

    // --- Query surface immutability ---

    function test_stagePenaltyMult_zeroForUnsealedEpoch() public {
        assertEq(penalty.stagePenaltyMult(node, 99), 0);
    }
}
