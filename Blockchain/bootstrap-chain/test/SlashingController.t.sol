// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {ISlashingController} from "../src/interfaces/ISlashingController.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

contract SlashingControllerTest is CoreProtocolTestBase {
    address internal node = makeAddr("node");

    function setUp() public {
        _deployCoreProtocol();
        _fund(node, 10_000e18);
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 1);
    }

    function test_stage1And2_noStakeTouched() public {
        uint256 before = registry.stakeOf(node);
        bytes memory sig1 = _signFault(node, 1, 1, false);
        slashing.submitFaultAttestation(node, 1, 1, false, sig1);
        assertEq(registry.stakeOf(node), before);

        bytes memory sig2 = _signFault(node, 2, 2, false);
        slashing.submitFaultAttestation(node, 2, 2, false, sig2);
        assertEq(registry.stakeOf(node), before);
    }

    function test_stage3_slashesConfiguredPercentPerEpoch() public {
        uint256 before = registry.stakeOf(node);
        bytes memory sig = _signFault(node, 3, 1, false);
        slashing.submitFaultAttestation(node, 3, 1, false, sig);

        uint256 expected = (before * 500) / 10_000; // 5%
        assertEq(registry.stakeOf(node), before - expected);
    }

    function test_stage3_cumulativeCap_respectsConfiguredCap() public {
        uint256 before = registry.stakeOf(node);
        for (uint256 i = 1; i <= 6; i++) {
            bytes memory sig = _signFault(node, 3, i, false);
            slashing.submitFaultAttestation(node, 3, i, false, sig);
        }
        // 6 x 5% would be 30%, capped at 25%.
        uint256 expectedRemaining = before - (before * 2_500) / 10_000;
        assertEq(registry.stakeOf(node), expectedRemaining);
    }

    function test_stage4_immediatePlusOngoing_andRegistryBan() public {
        uint256 before = registry.stakeOf(node);
        bytes memory sig1 = _signFault(node, 4, 1, false);
        slashing.submitFaultAttestation(node, 4, 1, false, sig1);
        uint256 afterImmediate = before - (before * 2_000) / 10_000; // 20%
        assertEq(registry.stakeOf(node), afterImmediate);
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Banned));

        bytes memory sig2 = _signFault(node, 4, 2, false);
        slashing.submitFaultAttestation(node, 4, 2, false, sig2);
        uint256 afterOngoing = afterImmediate - (afterImmediate * 500) / 10_000; // 5%
        assertEq(registry.stakeOf(node), afterOngoing);

        // Reactivation before ban duration elapses reverts.
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        vm.prank(node);
        vm.expectRevert();
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", required);
    }

    function test_nonHardwareViolation_bypassesProgressiveStages() public {
        uint256 before = registry.stakeOf(node);
        vm.prank(address(registry));
        slashing.reportNonHardwareViolation(node, ISlashingController.ViolationType.RegionVerificationFraud);

        uint256 expected = (before * 3_000) / 10_000; // 30%
        assertEq(registry.stakeOf(node), before - expected);
        assertEq(uint8(registry.statusOf(node)), uint8(INodeRegistry.NodeStatus.Banned));
        assertEq(slashing.stageOf(node), 4);
    }

    function test_recovery_restoresStageWithoutReturningStake() public {
        bytes memory sig = _signFault(node, 3, 1, false);
        slashing.submitFaultAttestation(node, 3, 1, false, sig);
        uint256 slashedStake = registry.stakeOf(node);

        for (uint256 i = 2; i <= 4; i++) {
            bytes memory cleanSig = _signFault(node, 0, i, true);
            slashing.submitFaultAttestation(node, 0, i, true, cleanSig);
        }

        assertEq(slashing.stageOf(node), 0);
        assertEq(registry.stakeOf(node), slashedStake);
    }

    function test_disposition_burnSendsToDeadAddress() public {
        uint256 before = registry.stakeOf(node);
        bytes memory sig = _signFault(node, 3, 1, false);
        slashing.submitFaultAttestation(node, 3, 1, false, sig);
        uint256 expected = (before * 500) / 10_000;
        assertEq(token.balanceOf(0x000000000000000000000000000000000000dEaD), expected);
    }

    function test_disposition_redistributeSendsToConfiguredTarget() public {
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.SLASHING_DISPOSITION, 1);

        uint256 before = registry.stakeOf(node);
        bytes memory sig = _signFault(node, 3, 1, false);
        slashing.submitFaultAttestation(node, 3, 1, false, sig);
        uint256 expected = (before * 500) / 10_000;
        assertEq(token.balanceOf(redistributionTarget), expected);
    }

    function test_unauthorizedSigner_reverts() public {
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(0xBADBAD, keccak256("garbage"));
        bytes memory badSig = abi.encodePacked(r, s, v);
        vm.expectRevert();
        slashing.submitFaultAttestation(node, 3, 1, false, badSig);
    }
}
