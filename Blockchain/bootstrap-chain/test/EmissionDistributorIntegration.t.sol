// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {NetworkRewardsEmission} from "../src/NetworkRewardsEmission.sol";
import {IEpochClock} from "../src/interfaces/IEpochClock.sol";
import {IFeeTreasury} from "../src/interfaces/IFeeTreasury.sol";
import {ISYMMinter} from "../src/interfaces/ISYMMinter.sol";

/// @notice #73 <-> RewardDistributor wiring: sealing an epoch pulls that
/// epoch's scheduled emission into the distributor and pays it out by score.
contract EmissionDistributorIntegrationTest is CoreProtocolTestBase {
    address internal nodeA = makeAddr("nodeA");
    address internal nodeB = makeAddr("nodeB");
    NetworkRewardsEmission internal emission;

    function setUp() public {
        _deployCoreProtocol();
        // MockERC20.mint(address,uint256) matches ISYMMinter, standing in for SYMToken.
        emission = new NetworkRewardsEmission(
            ISYMMinter(address(token)), address(rewards), IEpochClock(address(rewards)), IFeeTreasury(address(0))
        );
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REWARD_EMISSION_SOURCE, address(emission));

        address[2] memory nodes = [nodeA, nodeB];
        for (uint256 i; i < 2; ++i) {
            _fund(nodes[i], 10_000e18);
            uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
            vm.prank(nodes[i]);
            registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);
            vm.prank(verifierRole);
            registry.recordVerification(nodes[i], true, 0);
        }
    }

    function test_EpochEndMatchesClock() public view {
        assertEq(rewards.epochEndTimestamp(0), genesisTimestamp + EPOCH_LENGTH);
        assertEq(rewards.epochFinalityDeadline(0), rewards.epochEndTimestamp(0) + 1 hours);
    }

    function test_SealPullsScheduledEmissionAndPaysByScore() public {
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        rewards.submitMetricReport(nodeB, 0, 300, 9_500, _signMetric(nodeB, 0, 300, 9_500));
        assertEq(token.balanceOf(address(rewards)), 0);

        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        address[] memory nodes = new address[](2);
        (nodes[0], nodes[1]) = (nodeA, nodeB);
        uint256 beforeA = token.balanceOf(nodeA);
        uint256 beforeB = token.balanceOf(nodeB);
        rewards.sealEpoch(0, nodes);

        uint256 scheduled = emission.primaryEmission(0);
        assertEq(emission.emittedAmount(0), scheduled);
        uint256 paid = (token.balanceOf(nodeA) - beforeA) + (token.balanceOf(nodeB) - beforeB);
        // Capacity share is 92%; the 8% verifier share stays in the distributor (#81).
        assertApproxEqAbs(paid, (scheduled * 9_200) / 10_000, 2);
        assertApproxEqRel(token.balanceOf(nodeB) - beforeB, (token.balanceOf(nodeA) - beforeA) * 3, 0.01e18);
    }

    function test_PreEmittedEpochIsNotDoubleCounted() public {
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        emission.emitForEpoch(0); // anyone can trigger before seal
        address[] memory nodes = new address[](1);
        nodes[0] = nodeA;
        rewards.sealEpoch(0, nodes);
        assertEq(token.totalSupply() - 20_000e18, emission.primaryEmission(0)); // 2x 10k stake funding
    }
}
