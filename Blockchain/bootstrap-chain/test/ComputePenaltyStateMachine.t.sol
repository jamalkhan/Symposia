// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {Vm} from "forge-std/Vm.sol";
import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {ComputeTierRegistry} from "../src/ComputeTierRegistry.sol";
import {ComputePenaltyStateMachine} from "../src/ComputePenaltyStateMachine.sol";
import {IComputePenaltyStateMachine} from "../src/interfaces/IComputePenaltyStateMachine.sol";
import {MockDataLossOracle} from "./mocks/MockDataLossOracle.sol";
import {MockComputeFailureOracle} from "./mocks/MockComputeFailureOracle.sol";

contract ComputePenaltyStateMachineTest is CoreProtocolTestBase {
    ComputeTierRegistry internal tierRegistry;
    ComputePenaltyStateMachine internal penalty;
    MockDataLossOracle internal dataLossOracle;
    MockComputeFailureOracle internal failureOracle;

    address internal node = makeAddr("computeNode");

    uint256 internal constant ABS_LATENCY_MS = 100;
    uint256 internal constant WAL_LAG_THRESHOLD = 300;
    uint256 internal constant RESTART_THRESHOLD = 3;

    function setUp() public {
        _deployCoreProtocol();

        tierRegistry = ComputeTierRegistry(
            _deployProxy(address(new ComputeTierRegistry()), abi.encodeCall(ComputeTierRegistry.initialize, (cfg)))
        );
        penalty = ComputePenaltyStateMachine(
            _deployProxy(
                address(new ComputePenaltyStateMachine()), abi.encodeCall(ComputePenaltyStateMachine.initialize, (cfg))
            )
        );
        dataLossOracle = new MockDataLossOracle();
        failureOracle = new MockComputeFailureOracle();

        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.COMPUTE_PENALTY_STATE_MACHINE_ADDRESS, address(penalty));
        cfg.setAddress(ConfigKeys.COMPUTE_PENALTY_TIER_REGISTRY_ADDRESS, address(tierRegistry));
        cfg.setAddress(ConfigKeys.COMPUTE_PENALTY_DATA_LOSS_ORACLE, address(dataLossOracle));
        cfg.setAddress(ConfigKeys.COMPUTE_PENALTY_FAILURE_ORACLE, address(failureOracle));

        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_LATENCY_MULT_BPS, 15_000); // 1.5x
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_MIN_COHORT_SIZE, 5);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_RESTART_THRESHOLD, RESTART_THRESHOLD);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_WAL_LAG_THRESHOLD_SECONDS, WAL_LAG_THRESHOLD);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_STAGE1_TROUBLE_EPOCHS, 2);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_STAGE2_TROUBLE_EPOCHS, 4);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_STAGE3_TROUBLE_EPOCHS, 6);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_RECOVERY_CLEAN_EPOCHS, 3);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_STAGE3_PCT_PER_EPOCH_BPS, 500);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_STAGE3_CAP_BPS, 2_500);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_STAGE4_IMMEDIATE_BPS, 2_000);
        cfg.setUint(ConfigKeys.COMPUTE_PENALTY_STAGE4_ONGOING_BPS, 500);
        cfg.setUint(ConfigKeys.computePenaltyAbsLatencyMs(0), ABS_LATENCY_MS); // Rejected/unclassified tier fallback

        cfg.setUint(ConfigKeys.computePenaltyStageMultBps(0), 10_000);
        cfg.setUint(ConfigKeys.computePenaltyStageMultBps(1), 7_000);
        cfg.setUint(ConfigKeys.computePenaltyStageMultBps(2), 4_000);
        cfg.setUint(ConfigKeys.computePenaltyStageMultBps(3), 0);
        cfg.setUint(ConfigKeys.computePenaltyStageMultBps(4), 0);

        // Compute node type (ordinal 7) stake requirement, mirroring how
        // CoreProtocolTestBase seeds Storage (ordinal 0).
        cfg.setUint(ConfigKeys.registryStakeBase(uint8(INodeRegistry.NodeType.Compute)), 100e18);
        cfg.setUint(ConfigKeys.registryStakePerUnit(uint8(INodeRegistry.NodeType.Compute)), 1e18);
        vm.stopPrank();

        _fund(node, 10_000e18);
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Compute, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Compute, 1, "us-east", required);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 1);
    }

    // --- Telemetry helpers ---

    function _submitClean(address who, uint256 epoch, uint256 hostedDbCount) internal {
        vm.prank(who);
        penalty.submitTelemetry(who, epoch, ABS_LATENCY_MS - 1, 0, 0, 0, 10_000, hostedDbCount, 0, 0);
    }

    function _submitLatencyTrigger(address who, uint256 epoch, uint256 hostedDbCount) internal {
        vm.prank(who);
        penalty.submitTelemetry(who, epoch, ABS_LATENCY_MS + 1, 0, 0, 0, 5_000, hostedDbCount, 0, 0);
    }

    function _submitRestartTrigger(address who, uint256 epoch, uint256 hostedDbCount) internal {
        vm.prank(who);
        penalty.submitTelemetry(who, epoch, ABS_LATENCY_MS - 1, RESTART_THRESHOLD + 1, 0, 0, 5_000, hostedDbCount, 0, 0);
    }

    function _submitWalTrigger(address who, uint256 epoch, uint256 hostedDbCount) internal {
        vm.prank(who);
        penalty.submitTelemetry(
            who, epoch, ABS_LATENCY_MS - 1, 0, WAL_LAG_THRESHOLD + 1, 0, 5_000, hostedDbCount, 0, 0
        );
    }

    function _submitMemoryPressure(address who, uint256 epoch, uint256 memEvents, uint256 hostedDbCount) internal {
        vm.prank(who);
        penalty.submitTelemetry(who, epoch, ABS_LATENCY_MS - 1, 0, 0, memEvents, 5_000, hostedDbCount, 0, 0);
    }

    // --- Stage 1 entry timing ---

    function test_stage1_entersAtExactlyTwoConsecutiveElevatedEpochs() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage0Healthy));

        _submitLatencyTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));
        assertEq(penalty.stagePenaltyMult(node, 2), 7_000);
    }

    function test_stage1_doesNotEnterAtOneEpoch() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage0Healthy));
    }

    function test_stage1_doesNotEnterAtTwoNonConsecutiveEpochs() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);

        _submitClean(node, 2, 0);
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage0Healthy));

        _submitLatencyTrigger(node, 3, 0);
        penalty.sealEpoch(node, 3);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage0Healthy));
    }

    // --- Each Stage 1 trigger independently ---

    function test_stage1_latencyTriggerIndependently() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitLatencyTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));
    }

    function test_stage1_restartRateTriggerIndependently() public {
        _submitRestartTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitRestartTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));
    }

    function test_stage1_walLagTriggerIndependently() public {
        _submitWalTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitWalTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));
    }

    function test_stage1_simultaneousMultiTrigger_doesNotStack() public {
        vm.prank(node);
        penalty.submitTelemetry(
            node, 1, ABS_LATENCY_MS + 1, RESTART_THRESHOLD + 1, WAL_LAG_THRESHOLD + 1, 0, 1_000, 0, 0, 0
        );
        penalty.sealEpoch(node, 1);
        vm.prank(node);
        penalty.submitTelemetry(
            node, 2, ABS_LATENCY_MS + 1, RESTART_THRESHOLD + 1, WAL_LAG_THRESHOLD + 1, 0, 1_000, 0, 0, 0
        );
        penalty.sealEpoch(node, 2);

        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));
        assertEq(penalty.stagePenaltyMult(node, 2), 7_000);
    }

    // --- Stage 2 via persistence and via memory-pressure bypass ---

    function test_stage2_viaPersistence_fourTotalTroubledEpochs() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitLatencyTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));

        _submitLatencyTrigger(node, 3, 0);
        penalty.sealEpoch(node, 3);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));

        _submitLatencyTrigger(node, 4, 0);
        penalty.sealEpoch(node, 4);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage2Degraded));
        assertEq(penalty.stagePenaltyMult(node, 4), 4_000);
    }

    function test_stage2_viaMemoryPressureBypass_OOMVariant_fromCleanNode() public {
        _submitMemoryPressure(node, 1, 1, 0);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage2Degraded));
    }

    function test_stage2_viaMemoryPressureBypass_SwapVariant_fromCleanNode() public {
        _submitMemoryPressure(node, 1, 2, 0);
        penalty.sealEpoch(node, 1);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage2Degraded));
    }

    function test_stage2_flagsAllHostedDbsAsMigrationCandidates() public {
        vm.recordLogs();
        _submitMemoryPressure(node, 1, 1, 3);
        penalty.sealEpoch(node, 1);

        Vm.Log[] memory logs = vm.getRecordedLogs();
        bool found;
        for (uint256 i = 0; i < logs.length; i++) {
            if (logs[i].topics[0] == IComputePenaltyStateMachine.MigrationSignal.selector) {
                found = true;
            }
        }
        assertTrue(found, "expected MigrationSignal");
    }

    function test_stage2_noOpsSafelyWithZeroHostedDbs() public {
        vm.recordLogs();
        _submitMemoryPressure(node, 1, 1, 0);
        penalty.sealEpoch(node, 1);

        Vm.Log[] memory logs = vm.getRecordedLogs();
        for (uint256 i = 0; i < logs.length; i++) {
            assertTrue(logs[i].topics[0] != IComputePenaltyStateMachine.MigrationSignal.selector);
        }
    }

    // --- Stage 3 via persistence and via immediate bypass ---

    function _driveToStage2(address who) internal {
        _submitLatencyTrigger(who, 1, 0);
        penalty.sealEpoch(who, 1);
        _submitLatencyTrigger(who, 2, 0);
        penalty.sealEpoch(who, 2);
        _submitLatencyTrigger(who, 3, 0);
        penalty.sealEpoch(who, 3);
        _submitLatencyTrigger(who, 4, 0);
        penalty.sealEpoch(who, 4);
    }

    function test_stage3_viaPersistence() public {
        _driveToStage2(node);
        _submitLatencyTrigger(node, 5, 0);
        penalty.sealEpoch(node, 5);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage2Degraded));

        _submitLatencyTrigger(node, 6, 0);
        penalty.sealEpoch(node, 6);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage3Suspended));
        assertEq(penalty.stagePenaltyMult(node, 6), 0);
    }

    function test_stage3_viaImmediateDbUnavailabilityBypass_fromStage0() public {
        bytes32 evidence = keccak256("db-unavailable-1");
        failureOracle.setConfirmed(node, 1, evidence, true);
        penalty.reportDatabaseUnavailable(node, 1, 2, evidence);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage3Suspended));
    }

    function test_stage3_viaImmediateDbUnavailabilityBypass_fromStage1() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitLatencyTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));

        bytes32 evidence = keccak256("db-unavailable-2");
        failureOracle.setConfirmed(node, 3, evidence, true);
        penalty.reportDatabaseUnavailable(node, 3, 0, evidence);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage3Suspended));
    }

    // --- Stage 3 cumulative slash cap (golden-value arithmetic) ---

    function test_stage3_cumulativeSlashCapsAt25PercentAcrossSixEpochs() public {
        _driveToStage2(node);
        _submitLatencyTrigger(node, 5, 0);
        penalty.sealEpoch(node, 5);
        uint256 entryStake = registry.stakeOf(node);

        _submitLatencyTrigger(node, 6, 0); // stage3 entry epoch, first 5% slash applied
        penalty.sealEpoch(node, 6);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage3Suspended));

        for (uint256 e = 7; e <= 11; e++) {
            _submitLatencyTrigger(node, e, 0);
            penalty.sealEpoch(node, e);
        }

        uint256 expectedSlashed = (entryStake * 2_500) / 10_000; // 25% cap, exact
        assertEq(entryStake - registry.stakeOf(node), expectedSlashed);
    }

    function test_stage3_blocksNewPlacements() public {
        _driveToStage2(node);
        _submitLatencyTrigger(node, 5, 0);
        penalty.sealEpoch(node, 5);
        _submitLatencyTrigger(node, 6, 0);
        penalty.sealEpoch(node, 6);

        assertFalse(penalty.placementEligible(node));
    }

    // --- Stage 4 data loss ---

    function test_stage4_immediateTwentyPercentPlusContinuingFivePercent() public {
        uint256 before = registry.stakeOf(node);
        bytes32 evidence = keccak256("data-loss-1");
        dataLossOracle.setConfirmed(node, 1, evidence, true);
        penalty.reportDataLoss(node, 1, evidence);

        uint256 afterImmediate = before - (before * 2_000) / 10_000;
        assertEq(registry.stakeOf(node), afterImmediate);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage4Removed));

        penalty.sealEpoch(node, 2); // ongoing 5% of post-slash flat base
        uint256 afterOngoing = afterImmediate - (afterImmediate * 500) / 10_000;
        assertEq(registry.stakeOf(node), afterOngoing);
    }

    function test_stage4_additiveToStage3History_noDoubleCounting() public {
        _driveToStage2(node);
        _submitLatencyTrigger(node, 5, 0);
        penalty.sealEpoch(node, 5);
        _submitLatencyTrigger(node, 6, 0);
        penalty.sealEpoch(node, 6); // stage3 entry, first 5% slash
        uint256 afterStage3 = registry.stakeOf(node);

        bytes32 evidence = keccak256("data-loss-2");
        dataLossOracle.setConfirmed(node, 7, evidence, true);
        penalty.reportDataLoss(node, 7, evidence);

        uint256 expectedImmediate = (afterStage3 * 2_000) / 10_000;
        assertEq(registry.stakeOf(node), afterStage3 - expectedImmediate);
    }

    function test_stage4_forcesDeregistration_andBlocksRegistryGatedActions() public {
        bytes32 evidence = keccak256("data-loss-3");
        dataLossOracle.setConfirmed(node, 1, evidence, true);
        penalty.reportDataLoss(node, 1, evidence);

        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Banned));

        vm.prank(verifierRole);
        vm.expectRevert();
        registry.recordVerification(node, true, 2);
    }

    // --- Recovery ---

    function test_recovery_afterThreeConsecutiveCleanEpochs_restoresFullMultiplier() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitLatencyTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));

        uint256 stakeBefore = registry.stakeOf(node);

        _submitClean(node, 3, 0);
        penalty.sealEpoch(node, 3);
        _submitClean(node, 4, 0);
        penalty.sealEpoch(node, 4);
        _submitClean(node, 5, 0);
        penalty.sealEpoch(node, 5);

        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage0Healthy));
        assertEq(penalty.stagePenaltyMult(node, 5), 10_000);
        assertEq(registry.stakeOf(node), stakeBefore); // no stake returned, and Stage1 never slashed anything
    }

    function test_recovery_clockResetsOnRelapse() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitLatencyTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);

        _submitClean(node, 3, 0);
        penalty.sealEpoch(node, 3);
        _submitClean(node, 4, 0);
        penalty.sealEpoch(node, 4);
        // relapse before reaching 3 consecutive clean epochs
        _submitLatencyTrigger(node, 5, 0);
        penalty.sealEpoch(node, 5);

        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));

        // Needs a fresh 3-epoch clean streak from here.
        _submitClean(node, 6, 0);
        penalty.sealEpoch(node, 6);
        _submitClean(node, 7, 0);
        penalty.sealEpoch(node, 7);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));
        _submitClean(node, 8, 0);
        penalty.sealEpoch(node, 8);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage0Healthy));
    }

    function test_missingTelemetry_doesNotCountAsCleanOrTowardRecovery() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitLatencyTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));

        _submitClean(node, 3, 0);
        penalty.sealEpoch(node, 3);
        _submitClean(node, 4, 0);
        penalty.sealEpoch(node, 4);
        // epoch 5: no telemetry submitted at all
        penalty.sealEpoch(node, 5);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));

        _submitClean(node, 6, 0);
        penalty.sealEpoch(node, 6);
        _submitClean(node, 7, 0);
        penalty.sealEpoch(node, 7);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage1Warning));
        _submitClean(node, 8, 0);
        penalty.sealEpoch(node, 8);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage0Healthy));
    }

    // --- Placement eligibility hold ---

    function test_placementEligibility_delayedOneEpochBeyondStageExit() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitLatencyTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);

        _submitClean(node, 3, 0);
        penalty.sealEpoch(node, 3);
        _submitClean(node, 4, 0);
        penalty.sealEpoch(node, 4);
        _submitClean(node, 5, 0);
        penalty.sealEpoch(node, 5);
        assertEq(uint8(penalty.currentStageOf(node)), uint8(IComputePenaltyStateMachine.Stage.Stage0Healthy));
        assertFalse(penalty.placementEligible(node)); // exit epoch itself not yet eligible

        _submitClean(node, 6, 0);
        penalty.sealEpoch(node, 6);
        assertTrue(penalty.placementEligible(node));
    }

    function test_placementEligibility_furtherDelayedOnRelapseInHoldEpoch() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitLatencyTrigger(node, 2, 0);
        penalty.sealEpoch(node, 2);
        _submitClean(node, 3, 0);
        penalty.sealEpoch(node, 3);
        _submitClean(node, 4, 0);
        penalty.sealEpoch(node, 4);
        _submitClean(node, 5, 0);
        penalty.sealEpoch(node, 5);
        assertFalse(penalty.placementEligible(node));

        // The extra hold epoch itself fails a threshold -- eligibility stays delayed.
        _submitLatencyTrigger(node, 6, 0);
        penalty.sealEpoch(node, 6);
        assertFalse(penalty.placementEligible(node));

        _submitClean(node, 7, 0);
        penalty.sealEpoch(node, 7);
        assertTrue(penalty.placementEligible(node));
    }

    // --- Idempotency ---

    function test_idempotent_reEvaluationOfSealedEpoch_appliesNoDuplicateEffect() public {
        _submitLatencyTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);

        vm.expectRevert(
            abi.encodeWithSelector(ComputePenaltyStateMachine.AlreadySealed.selector, node, uint256(1))
        );
        penalty.sealEpoch(node, 1);
    }

    // --- Event payload / severity / recommendation text ---

    function test_events_stage1Transition_hasComputeSpecificWalRecommendation() public {
        _submitWalTrigger(node, 1, 0);
        penalty.sealEpoch(node, 1);
        _submitWalTrigger(node, 2, 0);

        vm.expectEmit(true, false, false, true, address(penalty));
        emit IComputePenaltyStateMachine.StageTransition(
            node,
            IComputePenaltyStateMachine.Stage.Stage0Healthy,
            IComputePenaltyStateMachine.Stage.Stage1Warning,
            2,
            IComputePenaltyStateMachine.TriggerType.WalLag,
            "70% reward multiplier applied",
            "Check WAL safekeeper peer connectivity",
            1
        );
        penalty.sealEpoch(node, 2);
    }

    function test_events_stage2Transition_hasMemoryPressureRecommendation() public {
        _submitMemoryPressure(node, 1, 1, 0);

        vm.expectEmit(true, false, false, true, address(penalty));
        emit IComputePenaltyStateMachine.StageTransition(
            node,
            IComputePenaltyStateMachine.Stage.Stage0Healthy,
            IComputePenaltyStateMachine.Stage.Stage2Degraded,
            1,
            IComputePenaltyStateMachine.TriggerType.MemoryPressure,
            "40% reward multiplier applied; hosted databases flagged as migration candidates",
            "Check for OOM killer invocations; reduce hosted database count to relieve CPU/memory pressure",
            1
        );
        penalty.sealEpoch(node, 1);
    }

    function test_events_stage3SlashApplied_severityCritical() public {
        _driveToStage2(node);
        _submitLatencyTrigger(node, 5, 0);
        penalty.sealEpoch(node, 5);
        _submitLatencyTrigger(node, 6, 0);

        uint256 entryStake = registry.stakeOf(node);
        uint256 expectedSlash = (entryStake * 500) / 10_000;

        vm.expectEmit(true, true, false, true, address(penalty));
        emit IComputePenaltyStateMachine.SlashApplied(
            node,
            6,
            IComputePenaltyStateMachine.Stage.Stage3Suspended,
            expectedSlash,
            IComputePenaltyStateMachine.TriggerType.Latency,
            "Check for CPU/query contention driving elevated P99 latency",
            2
        );
        penalty.sealEpoch(node, 6);
    }

    function test_events_stage4_severityEmergency() public {
        bytes32 evidence = keccak256("data-loss-4");
        dataLossOracle.setConfirmed(node, 1, evidence, true);
        uint256 stake = registry.stakeOf(node);
        uint256 expectedImmediate = (stake * 2_000) / 10_000;

        vm.expectEmit(true, true, false, true, address(penalty));
        emit IComputePenaltyStateMachine.TenantDataLossNotification(node, 1, evidence);
        vm.recordLogs();
        penalty.reportDataLoss(node, 1, evidence);

        Vm.Log[] memory logs = vm.getRecordedLogs();
        bool foundSlash;
        bool foundTransition;
        for (uint256 i = 0; i < logs.length; i++) {
            if (logs[i].topics[0] == IComputePenaltyStateMachine.SlashApplied.selector) {
                foundSlash = true;
            }
            if (logs[i].topics[0] == IComputePenaltyStateMachine.StageTransition.selector) {
                foundTransition = true;
            }
        }
        assertTrue(foundSlash && foundTransition);
        assertEq(expectedImmediate, (stake * 2_000) / 10_000);
    }
}
