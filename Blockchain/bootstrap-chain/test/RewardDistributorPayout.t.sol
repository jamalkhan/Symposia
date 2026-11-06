// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {ReliabilityRegistry} from "../src/ReliabilityRegistry.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

/// @dev Stand-in for SlashingController's `stageOf` read by the distributor.
contract MockStageSource {
    mapping(address => uint8) public stageOf;

    function setStage(address node, uint8 stage) external {
        stageOf[node] = stage;
    }
}

/// @notice #80: payout formula, reliability bonus normalization (#79),
/// stage penalties, exclusion, idempotency and the decision trail.
contract RewardDistributorPayoutTest is CoreProtocolTestBase {
    ReliabilityRegistry internal rel;
    MockStageSource internal stages;
    address internal challenger = makeAddr("challenger");
    address[] internal storageNodes;

    uint256 internal constant EMISSION = 1_000_000e18;
    uint256 internal constant POOL = (EMISSION * 9_200) / 10_000; // Storage weight is 100%

    function setUp() public {
        _deployCoreProtocol();
        token.mint(address(rewards), 1_000_000_000e18);

        rel = ReliabilityRegistry(
            address(new ERC1967Proxy(address(new ReliabilityRegistry()), abi.encodeCall(ReliabilityRegistry.initialize, (cfg))))
        );
        stages = new MockStageSource();
        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.REWARD_RELIABILITY_REGISTRY, address(rel));
        cfg.setAddress(ConfigKeys.RELIABILITY_FINALIZER, address(rewards));
        cfg.setAddress(ConfigKeys.RELIABILITY_CHALLENGER, challenger);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_POR_BPS, 200);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_PROBE_BPS, 100);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_TIMEOUT_BPS, 50);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_HEARTBEAT_BPS_PER_PCT, 0); // isolate challenge effects
        cfg.setUint(ConfigKeys.RELIABILITY_RECOVERY_BPS, 0);
        cfg.setUint(ConfigKeys.RELIABILITY_BONUS_WINDOW, 3); // short window keeps tests fast
        cfg.setUint(ConfigKeys.REWARD_RELIABILITY_BONUS_THRESHOLD_BPS, 9_800);
        cfg.setUint(ConfigKeys.REWARD_RELIABILITY_BONUS_BPS, 10_500);
        vm.stopPrank();

        for (uint256 i; i < 4; ++i) {
            address node = makeAddr(string(abi.encodePacked("storage", i)));
            _register(node, INodeRegistry.NodeType.Storage);
            storageNodes.push(node);
        }
    }

    function _register(address node, INodeRegistry.NodeType t) internal {
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.registryStakeBase(uint8(t)), 100e18);
        _fund(node, 10_000e18);
        uint256 required = registry.minStakeFor(t, 1);
        vm.prank(node);
        registry.register(t, 1, "us-east", required);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
    }

    function _report(address node, uint256 epoch, uint256 score) internal {
        rewards.submitMetricReport(node, epoch, score, 9_500, _signMetric(node, epoch, score, 9_500));
    }

    function _seal(uint256 epoch, address[] memory nodes) internal {
        vm.warp(rewards.epochFinalityDeadline(epoch) + 1);
        rewards.sealEpoch(epoch, nodes);
    }

    function _sumPayouts(uint256 epoch, address[] memory nodes) internal view returns (uint256 s) {
        for (uint256 i; i < nodes.length; ++i) {
            s += rewards.payoutOf(epoch, nodes[i]);
        }
    }

    function _reason(uint256 epoch, address node) internal view returns (RewardDistributor.SettlementReason) {
        return rewards.settlementOf(epoch, node).reason;
    }

    // --- FR1 formula ---

    function test_PayoutsProportionalToScoreAndSumToPool() public {
        uint256[4] memory scores = [uint256(100), 200, 300, 400];
        for (uint256 i; i < 4; ++i) {
            _report(storageNodes[i], 0, scores[i]);
        }
        _seal(0, storageNodes);
        for (uint256 i; i < 4; ++i) {
            assertApproxEqAbs(rewards.payoutOf(0, storageNodes[i]), (POOL * scores[i]) / 1000, 3);
            assertEq(uint8(_reason(0, storageNodes[i])), uint8(RewardDistributor.SettlementReason.Paid));
        }
        assertEq(_sumPayouts(0, storageNodes), POOL);
    }

    function test_ReliabilityScalesPayoutAndForfeitsShare() public {
        for (uint256 i; i < 4; ++i) {
            _report(storageNodes[i], 0, 100);
        }
        vm.prank(challenger);
        rel.recordChallengeOutcome(storageNodes[0], 0, ReliabilityRegistry.ChallengeKind.Por, false, 0, 1); // 0.98
        _seal(0, storageNodes);
        uint256 share = POOL / 4;
        assertEq(rewards.payoutOf(0, storageNodes[0]), (share * 9_800) / 10_000);
        assertEq(rewards.payoutOf(0, storageNodes[1]), share);
        assertEq(rewards.settlementOf(0, storageNodes[0]).reliabilityBps, 9_800);
        assertLe(_sumPayouts(0, storageNodes), POOL); // forfeited, not redistributed
    }

    function test_BonusNormalizedWithinTypeKeepsPoolExact() public {
        // Nodes 0 and 1 build a 3-epoch high streak; nodes 2 and 3 fall below 0.98 at epoch 0.
        for (uint256 e; e < 3; ++e) {
            for (uint256 i; i < 4; ++i) {
                _report(storageNodes[i], e, 100);
            }
            if (e == 0) {
                vm.startPrank(challenger);
                for (uint256 i = 2; i < 4; ++i) {
                    rel.recordChallengeOutcome(storageNodes[i], 0, ReliabilityRegistry.ChallengeKind.Por, false, 0, 1);
                    rel.recordChallengeOutcome(storageNodes[i], 0, ReliabilityRegistry.ChallengeKind.Por, false, 0, 2);
                }
                vm.stopPrank();
            }
            _seal(e, storageNodes);
        }
        // Epoch 2: nodes 0/1 eligible (score 1.0), nodes 2/3 at 0.96, not eligible.
        uint256 share = POOL / 4;
        assertTrue(rewards.settlementOf(2, storageNodes[0]).reliabilityBonus);
        assertFalse(rewards.settlementOf(2, storageNodes[2]).reliabilityBonus);
        assertEq(rewards.payoutOf(2, storageNodes[0]), (share * 10_500) / 10_000);
        uint256 adjustedTotal = 2 * share + 2 * ((share * 9_600) / 10_000);
        assertEq(_sumPayouts(2, storageNodes), adjustedTotal);
        assertLt(rewards.payoutOf(2, storageNodes[2]), (share * 9_600) / 10_000); // peers absorb the bonus
    }

    // --- FR7 stage penalties ---

    function test_StagePenaltiesApplyMultiplicatively() public {
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER, address(stages));
        stages.setStage(storageNodes[0], 1); // 0.70
        stages.setStage(storageNodes[1], 3); // 0
        stages.setStage(storageNodes[2], 4); // 0
        for (uint256 i; i < 4; ++i) {
            _report(storageNodes[i], 0, 100);
        }
        _seal(0, storageNodes);
        uint256 share = POOL / 4;
        assertEq(rewards.payoutOf(0, storageNodes[0]), (share * 7_000) / 10_000);
        assertEq(rewards.payoutOf(0, storageNodes[1]), 0);
        assertEq(rewards.payoutOf(0, storageNodes[2]), 0);
        assertEq(rewards.payoutOf(0, storageNodes[3]), share); // stage-zeroed share is not redistributed
        assertEq(uint8(_reason(0, storageNodes[1])), uint8(RewardDistributor.SettlementReason.ZeroStagePenalty));
        assertEq(uint8(_reason(0, storageNodes[2])), uint8(RewardDistributor.SettlementReason.ZeroStagePenalty));
    }

    // --- FR2 eligibility, FR9 exclusion ---

    function test_IneligibleNodeRecordedWithReason() public {
        _report(storageNodes[0], 0, 100);
        _seal(0, storageNodes); // nodes 1..3 never reported
        assertEq(rewards.payoutOf(0, storageNodes[0]), POOL);
        assertEq(uint8(_reason(0, storageNodes[1])), uint8(RewardDistributor.SettlementReason.Ineligible));
    }

    /// #81: verifiers are paid from their own 8% slice; Email IP and Compute stay excluded.
    function test_VerifierPaidFromItsSliceEmailIpAndComputeExcluded() public {
        address verifier = makeAddr("verifierNode");
        address emailIp = makeAddr("emailIpNode");
        address compute = makeAddr("computeNode");
        _register(verifier, INodeRegistry.NodeType.Verifier);
        _register(emailIp, INodeRegistry.NodeType.EmailIP);
        _register(compute, INodeRegistry.NodeType.Compute);
        address[] memory nodes = new address[](4);
        (nodes[0], nodes[1], nodes[2], nodes[3]) = (storageNodes[0], verifier, emailIp, compute);
        for (uint256 i; i < 4; ++i) {
            _report(nodes[i], 0, 100);
        }
        _seal(0, nodes); // previously reverted on the Compute ordinal
        assertEq(rewards.payoutOf(0, storageNodes[0]), POOL);
        assertEq(rewards.payoutOf(0, verifier), (EMISSION * 800) / 10_000, "sole verifier takes the 8% slice");
        assertEq(uint8(_reason(0, verifier)), uint8(RewardDistributor.SettlementReason.Paid));
        assertTrue(rel.isFinalized(verifier, 0));
        for (uint256 i = 2; i < 4; ++i) {
            assertEq(rewards.payoutOf(0, nodes[i]), 0);
            assertEq(uint8(_reason(0, nodes[i])), uint8(RewardDistributor.SettlementReason.ExcludedType));
        }
    }

    function test_MultiTypePoolsRespectWeights() public {
        address oltp = makeAddr("oltpNode");
        _register(oltp, INodeRegistry.NodeType.OLTP);
        vm.startPrank(configOwner);
        cfg.setUint(ConfigKeys.rewardTypeWeight(0), 6_000);
        cfg.setUint(ConfigKeys.rewardTypeWeight(1), 4_000);
        vm.stopPrank();
        address[] memory nodes = new address[](2);
        (nodes[0], nodes[1]) = (storageNodes[0], oltp);
        _report(nodes[0], 0, 1);
        _report(nodes[1], 0, 999);
        _seal(0, nodes);
        assertEq(rewards.payoutOf(0, nodes[0]), (POOL * 6_000) / 10_000);
        assertEq(rewards.payoutOf(0, nodes[1]), (POOL * 4_000) / 10_000);
    }

    // --- FR8 idempotency ---

    function test_RevertWhen_DuplicateNodeInBatch() public {
        _report(storageNodes[0], 0, 100);
        address[] memory nodes = new address[](2);
        (nodes[0], nodes[1]) = (storageNodes[0], storageNodes[0]);
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.DuplicateNode.selector, storageNodes[0]));
        rewards.sealEpoch(0, nodes);
    }

    function test_ResealRevertsAndNeverDoublePays() public {
        _report(storageNodes[0], 0, 100);
        _seal(0, storageNodes);
        uint256 bal = token.balanceOf(storageNodes[0]);
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.EpochAlreadySealed.selector, 0));
        rewards.sealEpoch(0, storageNodes);
        assertEq(token.balanceOf(storageNodes[0]), bal);
    }

    // --- FR10 trail ---

    function test_NodeSettledEventCarriesInputs() public {
        _report(storageNodes[0], 0, 100);
        address[] memory nodes = new address[](1);
        nodes[0] = storageNodes[0];
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        vm.expectEmit(true, true, true, true);
        emit RewardDistributor.NodeSettled(
            storageNodes[0], 0, RewardDistributor.SettlementReason.Paid, 100, 100, POOL, 10_000, 10_000, false, POOL
        );
        rewards.sealEpoch(0, nodes);
    }

    // --- FR3 invariant (fuzzed) ---

    function testFuzz_TypePoolNeverExceeded(uint16[4] memory scores, uint8 stageMask, uint8 porMask) public {
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER, address(stages));
        for (uint256 i; i < 4; ++i) {
            _report(storageNodes[i], 0, uint256(scores[i]) + 1);
            stages.setStage(storageNodes[i], uint8((stageMask >> (2 * i)) & 3));
            if ((porMask >> i) & 1 == 1) {
                vm.prank(challenger);
                rel.recordChallengeOutcome(storageNodes[i], 0, ReliabilityRegistry.ChallengeKind.Por, false, 0, i);
            }
        }
        _seal(0, storageNodes);
        assertLe(_sumPayouts(0, storageNodes), POOL);
    }
}

/// @notice Review fixes: seal completeness, ordering, loud reliability
/// failures, and the undistributed ledger.
contract RewardDistributorSealGuardsTest is RewardDistributorPayoutTest {
    function test_RevertWhen_SubsetOmitsReporter() public {
        _report(storageNodes[0], 0, 100);
        _report(storageNodes[1], 0, 100);
        address[] memory only = new address[](1);
        only[0] = storageNodes[0];
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.IncompleteNodeSet.selector, 0, 1, 2));
        rewards.sealEpoch(0, only); // would otherwise hand node 0 the whole pool
    }

    function test_RevertWhen_EmptyListWithReporters() public {
        _report(storageNodes[0], 0, 100);
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.IncompleteNodeSet.selector, 0, 0, 1));
        rewards.sealEpoch(0, new address[](0));
    }

    function test_ReporterCountedOnceAcrossResubmits() public {
        _report(storageNodes[0], 0, 100);
        _report(storageNodes[0], 0, 200);
        assertEq(rewards.reporterCount(0), 1);
    }

    function test_RevertWhen_SealOutOfOrder() public {
        vm.warp(epochs.epochStart(1));
        _report(storageNodes[0], 1, 100);
        vm.warp(rewards.epochFinalityDeadline(1) + 1);
        // Skipping ahead is rejected: epoch 0 must be sealed first.
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.EpochSealOutOfOrder.selector, 1, 0));
        rewards.sealEpoch(1, storageNodes);
        rewards.sealEpoch(0, new address[](0));
        rewards.sealEpoch(1, storageNodes);
        assertEq(rewards.nextSealableEpoch(), 2);
        // And re-sealing an earlier epoch is rejected.
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.EpochAlreadySealed.selector, 0));
        rewards.sealEpoch(0, storageNodes);
    }

    function test_RevertWhen_ReliabilityRegistryPaused() public {
        _report(storageNodes[0], 0, 100);
        vm.prank(timelockAddr);
        rel.pause();
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        vm.expectRevert(); // no silent neutral-1.0 fallback
        rewards.sealEpoch(0, storageNodes);
        assertFalse(rewards.isSealed(0));
    }

    function test_RevertWhen_FinalizerMisconfigured() public {
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.RELIABILITY_FINALIZER, makeAddr("wrong"));
        _report(storageNodes[0], 0, 100);
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        vm.expectRevert(abi.encodeWithSelector(ReliabilityRegistry.NotFinalizer.selector, address(rewards)));
        rewards.sealEpoch(0, storageNodes);
    }

    function test_UndistributedLedgerAndSweep() public {
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER, address(stages));
        stages.setStage(storageNodes[1], 3);
        _report(storageNodes[0], 0, 100);
        _report(storageNodes[1], 0, 100);
        _seal(0, storageNodes);
        // 8% verifier share + node 1's forfeited half of the pool.
        uint256 expected = EMISSION - rewards.payoutOf(0, storageNodes[0]);
        assertEq(rewards.undistributed(), expected);

        address sink = makeAddr("sink");
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.SweepExceedsUndistributed.selector, expected + 1, expected));
        vm.prank(timelockAddr);
        rewards.sweepUndistributed(sink, expected + 1);
        vm.prank(timelockAddr);
        rewards.sweepUndistributed(sink, expected);
        assertEq(token.balanceOf(sink), expected);
        assertEq(rewards.undistributed(), 0);
    }

    function test_RevertWhen_NonTimelockSweeps() public {
        vm.expectRevert();
        rewards.sweepUndistributed(address(this), 1);
    }
}
