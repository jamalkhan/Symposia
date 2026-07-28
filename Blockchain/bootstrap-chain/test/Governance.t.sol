// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {StakingNodeRegistryV2Mock} from "./mocks/StakingNodeRegistryV2Mock.sol";
import {StakingNodeRegistry} from "../src/StakingNodeRegistry.sol";

/// @notice Covers FR-6 / AC-1, AC-3, AC-9: UUPS upgrade gating and the
/// pause circuit breaker, using StakingNodeRegistry as the representative
/// contract (all four share the same GovernedUpgradeable base, so this
/// exercises the shared logic once rather than duplicating per-contract).
contract GovernanceTest is CoreProtocolTestBase {
    function setUp() public {
        _deployCoreProtocol();
    }

    function test_upgrade_directCallFromDeployerEOA_reverts() public {
        StakingNodeRegistryV2Mock v2 = new StakingNodeRegistryV2Mock();
        vm.expectRevert();
        registry.upgradeToAndCall(address(v2), "");
    }

    function test_upgrade_directCallFromArbitraryAddress_reverts() public {
        StakingNodeRegistryV2Mock v2 = new StakingNodeRegistryV2Mock();
        address stranger = makeAddr("stranger");
        vm.prank(stranger);
        vm.expectRevert();
        registry.upgradeToAndCall(address(v2), "");
    }

    function test_upgrade_fromTimelock_succeeds_andPreservesStorage() public {
        address node = makeAddr("node");
        _fund(node, 1_000e18);
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);

        StakingNodeRegistryV2Mock v2 = new StakingNodeRegistryV2Mock();
        vm.prank(timelockAddr);
        registry.upgradeToAndCall(address(v2), "");

        assertEq(StakingNodeRegistryV2Mock(address(registry)).version(), "v2-test");
        // Storage preserved across upgrade.
        assertEq(registry.stakeOf(node), required);
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.PendingVerification));
    }

    function test_pause_onlyTimelock_andBlocksStateMutation_notViews() public {
        address stranger = makeAddr("stranger");
        vm.prank(stranger);
        vm.expectRevert();
        registry.pause();

        vm.prank(timelockAddr);
        registry.pause();
        assertTrue(registry.paused());

        address node = makeAddr("node");
        _fund(node, 1_000e18);
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        vm.expectRevert();
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);

        // View functions remain callable while paused.
        registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);

        vm.prank(timelockAddr);
        registry.unpause();
        assertFalse(registry.paused());
    }

    function test_setConfig_onlyTimelock() public {
        address stranger = makeAddr("stranger");
        vm.prank(stranger);
        vm.expectRevert();
        registry.setConfig(cfg);
    }
}
