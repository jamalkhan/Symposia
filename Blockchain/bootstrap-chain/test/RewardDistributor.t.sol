// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

contract RewardDistributorTest is CoreProtocolTestBase {
    address internal nodeA = makeAddr("nodeA");
    address internal nodeB = makeAddr("nodeB");

    function setUp() public {
        _deployCoreProtocol();
        token.mint(address(rewards), 100_000_000e18);

        _registerAndVerify(nodeA);
        _registerAndVerify(nodeB);
    }

    function _registerAndVerify(address node) internal {
        _fund(node, 10_000e18);
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
    }

    function _nodes() internal view returns (address[] memory arr) {
        arr = new address[](2);
        arr[0] = nodeA;
        arr[1] = nodeB;
    }

    function _warpToFinality(uint256 epoch) internal {
        vm.warp(rewards.epochFinalityDeadline(epoch) + 1);
    }

    function test_sealEpoch_splitsByScore_andPaysCompliantNodes() public {
        uint256 beforeA = token.balanceOf(nodeA);
        uint256 beforeB = token.balanceOf(nodeB);
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        rewards.submitMetricReport(nodeB, 0, 300, 9_500, _signMetric(nodeB, 0, 300, 9_500));

        _warpToFinality(0);
        rewards.sealEpoch(0, _nodes());

        assertTrue(rewards.isSealed(0));
        uint256 payoutA = token.balanceOf(nodeA) - beforeA;
        uint256 payoutB = token.balanceOf(nodeB) - beforeB;
        assertGt(payoutA, 0);
        assertGt(payoutB, 0);
        // B submitted 3x A's score -> should receive ~3x reward.
        assertApproxEqRel(payoutB, payoutA * 3, 0.01e18);
    }

    function test_ineligibleNode_zeroReward_noRevert() public {
        uint256 beforeA = token.balanceOf(nodeA);
        uint256 beforeB = token.balanceOf(nodeB);
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        // nodeB never submits a metric report -> ineligible.

        _warpToFinality(0);
        rewards.sealEpoch(0, _nodes());

        assertGt(token.balanceOf(nodeA) - beforeA, 0);
        assertEq(token.balanceOf(nodeB), beforeB);
        assertEq(rewards.reserveBalanceOf(nodeB), 0);
    }

    function test_belowHeartbeatThreshold_heldInReserve() public {
        uint256 beforeA = token.balanceOf(nodeA);
        rewards.submitMetricReport(nodeA, 0, 100, 8_000, _signMetric(nodeA, 0, 100, 8_000)); // 80% < 90% threshold
        _warpToFinality(0);

        address[] memory single = new address[](1);
        single[0] = nodeA;
        rewards.sealEpoch(0, single);

        assertEq(token.balanceOf(nodeA), beforeA);
        assertGt(rewards.reserveBalanceOf(nodeA), 0);
    }

    function test_cannotSealBeforeFinalityWindow() public {
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        vm.expectRevert();
        rewards.sealEpoch(0, _nodes());
    }

    function test_cannotSealTwice() public {
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        _warpToFinality(0);
        rewards.sealEpoch(0, _nodes());

        vm.expectRevert();
        rewards.sealEpoch(0, _nodes());
    }

    function test_configChange_affectsFutureEpochOnly_notSealedEpoch() public {
        uint256 startBalance = token.balanceOf(nodeA);
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        _warpToFinality(0);
        address[] memory single = new address[](1);
        single[0] = nodeA;
        rewards.sealEpoch(0, single);
        uint256 payoutEpoch0 = token.balanceOf(nodeA) - startBalance;
        uint256 balanceAfterEpoch0 = token.balanceOf(nodeA);

        // Governance doubles emission before epoch 1 seals.
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.REWARD_EMISSION_PER_EPOCH, 2_000_000e18);

        rewards.submitMetricReport(nodeA, 1, 100, 9_500, _signMetric(nodeA, 1, 100, 9_500));
        _warpToFinality(1);
        rewards.sealEpoch(1, single);
        uint256 payoutEpoch1Delta = token.balanceOf(nodeA) - balanceAfterEpoch0;

        assertApproxEqRel(payoutEpoch1Delta, payoutEpoch0 * 2, 0.01e18);
    }
}
