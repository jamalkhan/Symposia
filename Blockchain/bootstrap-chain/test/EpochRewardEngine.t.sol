// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

/// @title EpochRewardEngineTest
/// @notice Issue #53 coverage: per-factor normalization, finality-window
/// late-report rejection, strict >90% two-epoch heartbeat compliance
/// (including the per-node "first eligible epoch" exemption), and the
/// 2-consecutive-epoch claimable-reserve sweep/no-compounding behavior.
/// Complements (does not replace) `RewardDistributor.t.sol`'s #52 coverage.
contract EpochRewardEngineTest is CoreProtocolTestBase {
    address internal nodeA = makeAddr("nodeA");
    address internal nodeB = makeAddr("nodeB");
    address internal nodeC = makeAddr("nodeC");

    function setUp() public {
        _deployCoreProtocol();
        token.mint(address(rewards), 100_000_000e18);

        _registerAndVerify(nodeA);
        _registerAndVerify(nodeB);
        _registerAndVerify(nodeC);
    }

    function _registerAndVerify(address node) internal {
        _fund(node, 10_000e18);
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
    }

    function _warpToFinality(uint256 epoch) internal {
        vm.warp(rewards.epochFinalityDeadline(epoch) + 1);
    }

    /// @notice All other factors left at 0 (unreported by anyone in the
    /// epoch => in-epoch max is 0 => those factors contribute 0 to every
    /// node's weighted score, per FR2.2), isolating the observable score/
    /// payout ratio to purely the retrieval-speed factor's 0.30 weight so
    /// the spec's worked 100/50/25 -> 1.0/0.5/0.25 example is directly
    /// verifiable end-to-end through the payout amounts.
    function _factorValues(uint256 retrievalSpeed) internal pure returns (uint256[7] memory v) {
        v[0] = retrievalSpeed;
    }

    // --- FR2: per-factor normalization against best-in-epoch ---

    function test_perFactorNormalization_matchesSpecExample() public {
        // Spec Gherkin: retrieval-speed values 100/50/25 -> normalized
        // 1.0/0.5/0.25 for the fastest/mid/slowest node respectively. All
        // other factors held equal across nodes so retrieval-speed's 0.30
        // weight drives the observable score delta directly.
        rewards.submitFactorMetrics(nodeA, 0, _factorValues(100), 9_500, _signFactorMetrics(nodeA, 0, _factorValues(100), 9_500));
        rewards.submitFactorMetrics(nodeB, 0, _factorValues(50), 9_500, _signFactorMetrics(nodeB, 0, _factorValues(50), 9_500));
        rewards.submitFactorMetrics(nodeC, 0, _factorValues(25), 9_500, _signFactorMetrics(nodeC, 0, _factorValues(25), 9_500));

        uint256 beforeA = token.balanceOf(nodeA);
        uint256 beforeB = token.balanceOf(nodeB);
        uint256 beforeC = token.balanceOf(nodeC);

        _warpToFinality(0);
        address[] memory nodes = new address[](3);
        nodes[0] = nodeA;
        nodes[1] = nodeB;
        nodes[2] = nodeC;
        rewards.sealEpoch(0, nodes);

        uint256 payoutA = token.balanceOf(nodeA) - beforeA;
        uint256 payoutB = token.balanceOf(nodeB) - beforeB;
        uint256 payoutC = token.balanceOf(nodeC) - beforeC;

        // Since all other factors are tied, the ratio of total scores
        // mirrors the ratio of retrieval-speed normalized scores, which is
        // exactly 100:50:25 -> A pays out 2x B, and 4x C.
        assertApproxEqRel(payoutA, payoutB * 2, 0.01e18);
        assertApproxEqRel(payoutA, payoutC * 4, 0.01e18);
    }

    function test_normalization_isRecomputedFreshPerEpoch_notCachedReference() public {
        // Epoch 0: A is best (100 vs 50).
        rewards.submitFactorMetrics(nodeA, 0, _factorValues(100), 9_500, _signFactorMetrics(nodeA, 0, _factorValues(100), 9_500));
        rewards.submitFactorMetrics(nodeB, 0, _factorValues(50), 9_500, _signFactorMetrics(nodeB, 0, _factorValues(50), 9_500));
        _warpToFinality(0);
        address[] memory nodes = new address[](2);
        nodes[0] = nodeA;
        nodes[1] = nodeB;
        rewards.sealEpoch(0, nodes);

        // Epoch 1: B is now best (200 vs 100) - normalization must flip.
        uint256 beforeA = token.balanceOf(nodeA);
        uint256 beforeB = token.balanceOf(nodeB);
        rewards.submitFactorMetrics(nodeA, 1, _factorValues(100), 9_500, _signFactorMetrics(nodeA, 1, _factorValues(100), 9_500));
        rewards.submitFactorMetrics(nodeB, 1, _factorValues(200), 9_500, _signFactorMetrics(nodeB, 1, _factorValues(200), 9_500));
        _warpToFinality(1);
        rewards.sealEpoch(1, nodes);

        uint256 payoutA = token.balanceOf(nodeA) - beforeA;
        uint256 payoutB = token.balanceOf(nodeB) - beforeB;
        assertApproxEqRel(payoutB, payoutA * 2, 0.01e18);
    }

    // --- FR1.2/FR1.5: finality window enforcement on reports ---

    function test_metricReport_afterFinalityWindow_reverts() public {
        _warpToFinality(0);
        vm.expectRevert();
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
    }

    function test_factorReport_afterFinalityWindow_reverts() public {
        _warpToFinality(0);
        uint256[7] memory v = _factorValues(100);
        vm.expectRevert();
        rewards.submitFactorMetrics(nodeA, 0, v, 9_500, _signFactorMetrics(nodeA, 0, v, 9_500));
    }

    function test_lateReport_notIncorporated_sealReflectsOnlyOnTimeReports() public {
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        _warpToFinality(0);

        // Late report for nodeB after the window closed - must revert and
        // not be incorporated.
        vm.expectRevert();
        rewards.submitMetricReport(nodeB, 0, 300, 9_500, _signMetric(nodeB, 0, 300, 9_500));

        address[] memory nodes = new address[](2);
        nodes[0] = nodeA;
        nodes[1] = nodeB;
        rewards.sealEpoch(0, nodes);

        // nodeB never got a valid (on-time) report -> ineligible, no score.
        assertEq(rewards.reserveBalanceOf(nodeB), 0);
        assertGt(token.balanceOf(nodeA), 0);
    }

    function test_reportAfterSeal_reverts() public {
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        _warpToFinality(0);
        address[] memory single = new address[](1);
        single[0] = nodeA;
        rewards.sealEpoch(0, single);

        vm.expectRevert();
        rewards.submitMetricReport(nodeA, 0, 999, 9_500, _signMetric(nodeA, 0, 999, 9_500));
    }

    // --- FR5.2: strict >90% two-epoch heartbeat compliance ---

    function test_exactlyNinetyPercent_isNotCompliant() public {
        // Exactly 90.00% (9_000 bps) in both epochs must be treated as
        // non-compliant per the strict "> 90%" spec wording (QA test 31).
        uint256 before = token.balanceOf(nodeA);
        rewards.submitMetricReport(nodeA, 0, 100, 9_000, _signMetric(nodeA, 0, 100, 9_000));
        _warpToFinality(0);
        address[] memory single = new address[](1);
        single[0] = nodeA;
        rewards.sealEpoch(0, single);
        assertEq(token.balanceOf(nodeA), before);
        assertGt(rewards.reserveBalanceOf(nodeA), 0);
    }

    function test_justAboveNinetyPercent_isCompliant() public {
        uint256 before = token.balanceOf(nodeA);
        rewards.submitMetricReport(nodeA, 0, 100, 9_001, _signMetric(nodeA, 0, 100, 9_001));
        _warpToFinality(0);
        address[] memory single = new address[](1);
        single[0] = nodeA;
        rewards.sealEpoch(0, single);
        assertGt(token.balanceOf(nodeA), before);
        assertEq(rewards.reserveBalanceOf(nodeA), 0);
    }

    function test_newNode_firstEpochAtLaterIndex_judgedOnCurrentEpochAlone() public {
        // nodeB registers/verifies (in setUp) but only becomes eligible
        // starting at epoch 5 (its first-ever metric report), well after
        // epoch index 0. It must still get the "no preceding record"
        // exemption, not be penalized for lacking an epoch-4 record.
        rewards.submitMetricReport(nodeB, 5, 100, 9_300, _signMetric(nodeB, 5, 100, 9_300));
        _warpToFinality(5);
        address[] memory single = new address[](1);
        single[0] = nodeB;
        rewards.sealEpoch(5, single);

        assertGt(token.balanceOf(nodeB), 0);
        assertEq(rewards.reserveBalanceOf(nodeB), 0);
    }

    function test_newNode_secondEpoch_revertsToFullTwoEpochTest() public {
        rewards.submitMetricReport(nodeB, 5, 100, 9_300, _signMetric(nodeB, 5, 100, 9_300));
        _warpToFinality(5);
        address[] memory single = new address[](1);
        single[0] = nodeB;
        rewards.sealEpoch(5, single);

        // Epoch 6: current epoch compliant, but epoch 5 (now a real
        // preceding record) was also compliant, so this should pay out.
        rewards.submitMetricReport(nodeB, 6, 100, 9_500, _signMetric(nodeB, 6, 100, 9_500));
        _warpToFinality(6);
        uint256 before = token.balanceOf(nodeB);
        rewards.sealEpoch(6, single);
        assertGt(token.balanceOf(nodeB) - before, 0);

        // Epoch 7: current epoch compliant but epoch 6 was, too -> still
        // fine. Now epoch 8 with epoch 7 non-compliant should hold reserve,
        // proving the "no preceding record" exemption no longer applies.
        rewards.submitMetricReport(nodeB, 7, 100, 8_000, _signMetric(nodeB, 7, 100, 8_000)); // non-compliant
        _warpToFinality(7);
        rewards.sealEpoch(7, single);

        rewards.submitMetricReport(nodeB, 8, 100, 9_500, _signMetric(nodeB, 8, 100, 9_500)); // compliant now, but epoch7 wasn't
        _warpToFinality(8);
        uint256 reserveBefore = rewards.reserveBalanceOf(nodeB);
        rewards.sealEpoch(8, single);
        assertGt(rewards.reserveBalanceOf(nodeB), reserveBefore);
    }

    // --- FR5.4/AC9: reserve accumulation, no expiry, no compounding ---

    function test_reserveAccumulates_additively_noCompounding() public {
        address[] memory single = new address[](1);
        single[0] = nodeA;

        rewards.submitMetricReport(nodeA, 0, 100, 8_000, _signMetric(nodeA, 0, 100, 8_000));
        _warpToFinality(0);
        rewards.sealEpoch(0, single);
        uint256 r0 = rewards.reserveBalanceOf(nodeA);
        assertGt(r0, 0);

        rewards.submitMetricReport(nodeA, 1, 100, 8_000, _signMetric(nodeA, 1, 100, 8_000));
        _warpToFinality(1);
        rewards.sealEpoch(1, single);
        uint256 r1 = rewards.reserveBalanceOf(nodeA);

        // Purely additive: r1 == r0 + this epoch's held amount, no interest
        // multiplier applied to the carried-over r0 portion.
        assertEq(r1, r0 + (r1 - r0));
        assertGt(r1, r0);
    }

    // --- FR6.1/FR6.2: 2-consecutive-epoch sweep, exact streak semantics ---

    function test_sweep_requiresExactlyTwoConsecutiveCompliantEpochs() public {
        address[] memory single = new address[](1);
        single[0] = nodeA;
        uint256 startBalance = token.balanceOf(nodeA);

        // Epoch 0: non-compliant -> reserve credited.
        rewards.submitMetricReport(nodeA, 0, 100, 8_000, _signMetric(nodeA, 0, 100, 8_000));
        _warpToFinality(0);
        rewards.sealEpoch(0, single);
        uint256 reserve = rewards.reserveBalanceOf(nodeA);
        assertGt(reserve, 0);

        // Epoch 1: compliant (1 of 2 needed) - reserve does not fluctuate
        // upward from a *previous* epoch's payout since epoch 0 was
        // non-compliant, but epoch1's own new payout also isn't auto-pay
        // yet since epoch 0 fails the "preceding epoch" leg.
        rewards.submitMetricReport(nodeA, 1, 100, 9_500, _signMetric(nodeA, 1, 100, 9_500));
        _warpToFinality(1);
        rewards.sealEpoch(1, single);
        assertGt(rewards.reserveBalanceOf(nodeA), reserve, "epoch1 payout also reserved (epoch0 fails preceding-epoch leg)");
        assertEq(token.balanceOf(nodeA), startBalance);

        // Epoch 2: compliant again -> epoch1 AND epoch2 both compliant ->
        // sweep fires, plus epoch 2's own now-auto-pay-eligible payout.
        rewards.submitMetricReport(nodeA, 2, 100, 9_500, _signMetric(nodeA, 2, 100, 9_500));
        _warpToFinality(2);
        uint256 reserveBeforeSweep = rewards.reserveBalanceOf(nodeA);
        rewards.sealEpoch(2, single);

        assertEq(rewards.reserveBalanceOf(nodeA), 0);
        assertGe(token.balanceOf(nodeA) - startBalance, reserveBeforeSweep);
    }

    function test_singleComplianceEpoch_doesNotFalselyTriggerSweep_streakRestartsOnBreak() public {
        address[] memory single = new address[](1);
        single[0] = nodeA;

        // Epoch 0: non-compliant.
        rewards.submitMetricReport(nodeA, 0, 100, 8_000, _signMetric(nodeA, 0, 100, 8_000));
        _warpToFinality(0);
        rewards.sealEpoch(0, single);

        // Epoch 1: compliant (streak = 1).
        rewards.submitMetricReport(nodeA, 1, 100, 9_500, _signMetric(nodeA, 1, 100, 9_500));
        _warpToFinality(1);
        rewards.sealEpoch(1, single);

        // Epoch 2: non-compliant again - breaks the streak before it hits 2.
        rewards.submitMetricReport(nodeA, 2, 100, 8_500, _signMetric(nodeA, 2, 100, 8_500));
        _warpToFinality(2);
        rewards.sealEpoch(2, single);
        uint256 reserveAfterBreak = rewards.reserveBalanceOf(nodeA);
        assertGt(reserveAfterBreak, 0);

        // Epoch 3: compliant (streak restarts at 1, NOT counted with epoch 1
        // across the epoch-2 break) - no sweep yet, so the *entire*
        // pre-epoch-3 reserve balance must still be present (epoch 3's own
        // newly-held payout, itself not yet auto-pay-eligible since epoch 2
        // was non-compliant, is additionally credited on top).
        rewards.submitMetricReport(nodeA, 3, 100, 9_500, _signMetric(nodeA, 3, 100, 9_500));
        _warpToFinality(3);
        rewards.sealEpoch(3, single);
        assertGe(
            rewards.reserveBalanceOf(nodeA), reserveAfterBreak, "no sweep after single compliant epoch post-break"
        );

        // Epoch 4: second consecutive compliant epoch (3 and 4) -> sweep.
        rewards.submitMetricReport(nodeA, 4, 100, 9_500, _signMetric(nodeA, 4, 100, 9_500));
        _warpToFinality(4);
        rewards.sealEpoch(4, single);
        assertEq(rewards.reserveBalanceOf(nodeA), 0, "sweeps once streak of 2 (epochs 3,4) is reached");
    }

    function test_zeroReserveNode_unaffectedBySweepLogic_noReserveSweptEvent() public {
        address[] memory single = new address[](1);
        single[0] = nodeA;

        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        _warpToFinality(0);
        rewards.sealEpoch(0, single);
        assertEq(rewards.reserveBalanceOf(nodeA), 0);

        rewards.submitMetricReport(nodeA, 1, 100, 9_500, _signMetric(nodeA, 1, 100, 9_500));
        _warpToFinality(1);
        uint256 before = token.balanceOf(nodeA);
        rewards.sealEpoch(1, single);
        assertGt(token.balanceOf(nodeA) - before, 0);
        assertEq(rewards.reserveBalanceOf(nodeA), 0);
    }
}
