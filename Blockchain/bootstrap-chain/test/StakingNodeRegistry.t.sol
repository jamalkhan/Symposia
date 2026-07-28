// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {StakingNodeRegistry} from "../src/StakingNodeRegistry.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

contract StakingNodeRegistryTest is CoreProtocolTestBase {
    address internal node = makeAddr("node");

    function setUp() public {
        _deployCoreProtocol();
        _fund(node, 10_000e18);
    }

    function test_register_atExactMinStake_succeeds() public {
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 5);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 5, "us-east", required);

        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.PendingVerification));
        assertEq(registry.stakeOf(node), required);
    }

    function test_register_belowMinStake_reverts() public {
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 5);
        vm.prank(node);
        vm.expectRevert(abi.encodeWithSelector(StakingNodeRegistry.InsufficientStake.selector, required - 1, required));
        registry.register(INodeRegistry.NodeType.Storage, 5, "us-east", required - 1);
    }

    function test_topUp_byAnyAddress_succeeds() public {
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);

        address stranger = makeAddr("stranger");
        _fund(stranger, 50e18);
        vm.prank(stranger);
        token.approve(address(registry), type(uint256).max);
        vm.prank(stranger);
        registry.topUp(node, 10e18);

        assertEq(registry.stakeOf(node), required + 10e18);
    }

    function test_unstake_beforeCooldown_reverts_afterCooldown_succeeds() public {
        uint256 amount = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", amount);

        vm.prank(node);
        registry.requestUnstake(amount);

        vm.prank(node);
        vm.expectRevert();
        registry.withdrawUnstake();

        vm.warp(block.timestamp + 21 days);
        uint256 balanceBefore = token.balanceOf(node);
        vm.prank(node);
        registry.withdrawUnstake();
        assertEq(token.balanceOf(node), balanceBefore + amount);
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Unregistered));
    }

    function test_partialUnstake_belowMinimum_reverts() public {
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 10);
        vm.startPrank(node);
        registry.register(INodeRegistry.NodeType.Storage, 10, "us-east", required + 5e18);
        vm.expectRevert();
        registry.requestUnstake(5e18 + 1); // would drop below `required`
        vm.stopPrank();
    }

    function test_partialUnstake_aboveMinimum_succeeds() public {
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 10);
        vm.startPrank(node);
        registry.register(INodeRegistry.NodeType.Storage, 10, "us-east", required + 5e18);
        registry.requestUnstake(5e18);
        vm.stopPrank();
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Deregistering));
    }

    function test_bannedIdentity_cannotReactivate_untilBanExpires() public {
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);

        vm.prank(address(slashing));
        registry.banNode(node, block.timestamp + 90 days);

        vm.prank(node);
        vm.expectRevert();
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);

        vm.warp(block.timestamp + 90 days + 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.PendingVerification));
    }

    function test_recordVerification_onlyVerifierRole() public {
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);

        vm.prank(node);
        vm.expectRevert();
        registry.recordVerification(node, true, 1);

        vm.prank(verifierRole);
        registry.recordVerification(node, true, 1);
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Active));
        assertEq(registry.lastVerifiedEpochOf(node), 1);
    }

    function test_overcommitment_triggersSlashingHook() public {
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 1);

        // Simulate stake dropping below minimum via a slash from the controller.
        vm.prank(address(slashing));
        registry.applySlash(node, required, address(0));

        registry.checkOvercommitment(node);
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Banned));
    }
}
