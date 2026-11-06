// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

/// @notice Issue #160: the Blob Gateway Node Service earns from its own capacity type
/// pool (strawman: 10% of the capacity slice next to Storage's 90%).
contract BlobGatewayRewardsTest is CoreProtocolTestBase {
    INodeRegistry.NodeType internal constant STORAGE = INodeRegistry.NodeType.Storage;
    INodeRegistry.NodeType internal constant GATEWAY = INodeRegistry.NodeType.BlobGateway;
    uint256 internal constant CAPACITY = (1_000_000e18 * 9_200) / 10_000;
    uint256 internal constant STORAGE_POOL = (CAPACITY * 9_000) / 10_000;
    uint256 internal constant GATEWAY_POOL = (CAPACITY * 1_000) / 10_000;

    address internal g1 = makeAddr("gateway1");
    address internal g2 = makeAddr("gateway2");

    function setUp() public {
        _deployCoreProtocol();
        token.mint(address(rewards), 1_000_000_000e18);
        vm.startPrank(configOwner);
        cfg.setUint(ConfigKeys.rewardTypeWeight(0), 9_000);
        cfg.setUint(ConfigKeys.rewardTypeWeight(8), 1_000);
        cfg.setUint(ConfigKeys.registryStakeBase(8), 40e18);
        vm.stopPrank();
    }

    function _register(address node, INodeRegistry.NodeType s, uint256 stake) internal {
        _fund(node, 10_000e18);
        vm.prank(node);
        registry.registerService(s, 0, "us-east", stake);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
    }

    function _values(uint256 uptime, uint256 success, uint256 p95, uint256 bytesServed)
        internal
        pure
        returns (uint256[] memory v)
    {
        v = new uint256[](4);
        (v[0], v[1], v[2], v[3]) = (uptime, success, p95, bytesServed);
    }

    function _report(address node, uint256[] memory values) internal {
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19Ethereum Signed Message:\n32",
                keccak256(abi.encode(address(rewards), node, uint8(GATEWAY), uint256(0), values, uint256(9_500)))
            )
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(metricSignerPk, digest);
        rewards.submitServiceFactorMetrics(node, GATEWAY, 0, values, 9_500, abi.encodePacked(r, s, v));
    }

    function _seal(address[] memory nodes) internal {
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        rewards.sealEpoch(0, nodes);
    }

    function test_StorageAndGatewayOnOneNodeArePaidFromSeparatePools() public {
        _fund(g1, 10_000e18);
        vm.startPrank(g1);
        registry.registerService(STORAGE, 0, "us-east", 100e18);
        registry.registerService(GATEWAY, 0, "us-east", 40e18);
        vm.stopPrank();
        vm.prank(verifierRole);
        registry.recordVerification(g1, true, 0);
        rewards.submitMetricReport(g1, 0, 100, 9_500, _signMetric(g1, 0, 100, 9_500));
        _report(g1, _values(10_000, 10_000, 100, 1e9));

        uint256 before = token.balanceOf(g1);
        address[] memory nodes = new address[](1);
        nodes[0] = g1;
        _seal(nodes);

        assertEq(rewards.payoutOf(0, g1), STORAGE_POOL);
        assertEq(rewards.payoutOf(0, rewards.accountOf(g1, GATEWAY)), GATEWAY_POOL);
        assertEq(token.balanceOf(g1), before + STORAGE_POOL + GATEWAY_POOL);
    }

    function test_FasterGatewayScoresHigherOnLatency() public {
        _register(g1, GATEWAY, 40e18);
        _register(g2, GATEWAY, 40e18);
        _report(g1, _values(10_000, 10_000, 100, 1e9));
        _report(g2, _values(10_000, 10_000, 200, 1e9));
        address[] memory nodes = new address[](2);
        (nodes[0], nodes[1]) = (g1, g2);
        _seal(nodes);

        uint256 s1 = rewards.settlementOf(0, g1).score;
        uint256 s2 = rewards.settlementOf(0, g2).score;
        assertEq(s1, 1e18, "best on every factor");
        assertEq(s2, 0.9e18, "latency factor 0.5 x 20% weight");
        assertGt(rewards.payoutOf(0, g1), rewards.payoutOf(0, g2));
        assertLe(rewards.payoutOf(0, g1) + rewards.payoutOf(0, g2), GATEWAY_POOL);
    }

    function test_RevertWhen_GatewayReportHasWrongFactorCount() public {
        _register(g1, GATEWAY, 40e18);
        uint256[] memory three = new uint256[](3);
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.FactorCountMismatch.selector, 3, 4));
        rewards.submitServiceFactorMetrics(g1, GATEWAY, 0, three, 9_500, "");
    }

    function test_UnderStakedGatewayIsIneligible() public {
        _register(g1, GATEWAY, 40e18);
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.registryStakeBase(8), 40e18 + 1);
        _report(g1, _values(10_000, 10_000, 100, 1e9));
        address[] memory nodes = new address[](1);
        nodes[0] = g1;
        _seal(nodes);
        assertEq(uint8(rewards.settlementOf(0, g1).reason), uint8(RewardDistributor.SettlementReason.Ineligible));
    }

    function test_OnlyGatewaysReportingPaysOnlyTheGatewayPool() public {
        _register(g1, GATEWAY, 40e18);
        _report(g1, _values(10_000, 10_000, 100, 1e9));
        address[] memory nodes = new address[](1);
        nodes[0] = g1;
        _seal(nodes);
        assertEq(rewards.payoutOf(0, g1), GATEWAY_POOL);
        assertEq(rewards.undistributed(), 1_000_000e18 - GATEWAY_POOL, "Storage pool and verifier slice retained");
    }
}
