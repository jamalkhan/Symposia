// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {ISlashingController} from "../src/interfaces/ISlashingController.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {SlashingController} from "../src/SlashingController.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

/// @notice Issue #166: hardware-fault penalty stages are tracked per Node Service.
contract ServicePenaltyStagesTest is CoreProtocolTestBase {
    INodeRegistry.NodeType internal constant STORAGE = INodeRegistry.NodeType.Storage;
    INodeRegistry.NodeType internal constant VERIFIER = INodeRegistry.NodeType.Verifier;
    address internal node = makeAddr("node");

    function setUp() public {
        _deployCoreProtocol();
        token.mint(address(rewards), 1_000_000_000e18);
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.registryStakeBase(5), 1_000e18);
        _fund(node, 100_000e18);
        vm.startPrank(node);
        registry.registerService(STORAGE, 0, "us-east", 1_000e18);
        registry.registerService(VERIFIER, 0, "us-east", 1_000e18);
        vm.stopPrank();
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
    }

    function _attest(INodeRegistry.NodeType service, uint8 stage, uint256 epoch) internal {
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19Ethereum Signed Message:\n32",
                keccak256(abi.encode(address(slashing), node, uint8(service), stage, epoch, true))
            )
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(faultSignerPk, digest);
        slashing.submitServiceFaultAttestation(node, service, stage, epoch, true, abi.encodePacked(r, s, v));
    }

    function test_StageIsPerService() public {
        _attest(STORAGE, 2, 0);
        assertEq(slashing.stageOf(node, STORAGE), 2);
        assertEq(slashing.stageOf(node, VERIFIER), 0);
        assertEq(slashing.stageOf(node), 2, "legacy view: highest across services");
    }

    function test_Stage3SlashesOnlyThatService() public {
        _attest(STORAGE, 3, 0);
        assertEq(registry.serviceStakeOf(node, STORAGE), 950e18, "5% of the Storage stake");
        assertEq(registry.serviceStakeOf(node, VERIFIER), 1_000e18);
    }

    function test_Stage4BansOnlyThatService() public {
        _attest(STORAGE, 4, 0);
        assertEq(uint8(registry.serviceStatusOf(node, STORAGE)), uint8(INodeRegistry.NodeStatus.Banned));
        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.Active));
        assertEq(registry.serviceStakeOf(node, VERIFIER), 1_000e18);
    }

    function test_RevertWhen_AttestationReplayedForAnotherService() public {
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19Ethereum Signed Message:\n32",
                keccak256(abi.encode(address(slashing), node, uint8(STORAGE), uint8(2), uint256(0), true))
            )
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(faultSignerPk, digest);
        vm.expectRevert(SlashingController.InvalidSignature.selector);
        slashing.submitServiceFaultAttestation(node, VERIFIER, 2, 0, true, abi.encodePacked(r, s, v));
    }

    function test_RecoveryIsPerService() public {
        _attest(STORAGE, 1, 0);
        _attest(VERIFIER, 1, 0);
        for (uint256 e = 1; e <= 3; e++) _attest(STORAGE, 0, e);
        assertEq(slashing.stageOf(node, STORAGE), 0, "recovered after 3 clean epochs");
        assertEq(slashing.stageOf(node, VERIFIER), 1, "Verifier still in Stage 1");
    }

    function test_NodeLevelViolationStillHitsEveryService() public {
        vm.prank(address(registry));
        slashing.reportNonHardwareViolation(node, ISlashingController.ViolationType.RegionVerificationFraud);
        assertEq(slashing.stageOf(node, STORAGE), 4);
        assertEq(slashing.stageOf(node, VERIFIER), 4);
        assertEq(uint8(registry.serviceStatusOf(node, VERIFIER)), uint8(INodeRegistry.NodeStatus.Banned));
    }

    function test_StorageStageDoesNotCutVerifierPay() public {
        _attest(STORAGE, 3, 0); // Storage pays 0 at Stage 3
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19Ethereum Signed Message:\n32",
                keccak256(abi.encode(address(rewards), node, uint8(VERIFIER), uint256(0), uint256(100), uint256(9_500)))
            )
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(metricSignerPk, digest);
        rewards.submitServiceMetricReport(node, VERIFIER, 0, 100, 9_500, abi.encodePacked(r, s, v));
        rewards.submitMetricReport(node, 0, 100, 9_500, _signMetric(node, 0, 100, 9_500));
        address[] memory nodes = new address[](1);
        nodes[0] = node;
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        rewards.sealEpoch(0, nodes);

        assertEq(uint8(rewards.settlementOf(0, node).reason), uint8(RewardDistributor.SettlementReason.ZeroStagePenalty));
        address verifierAccount = rewards.accountOf(node, VERIFIER);
        assertEq(rewards.payoutOf(0, verifierAccount), (1_000_000e18 * 800) / 10_000, "full verifier slice");
    }
}
