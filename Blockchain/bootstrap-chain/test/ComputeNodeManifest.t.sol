// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {ComputeTierRegistry, IFoundationWitnessSource} from "../src/ComputeTierRegistry.sol";
import {ComputeNodeManifest} from "../src/ComputeNodeManifest.sol";

/// @notice Test suite for issue #90: compute node staking, onboarding, and
/// capacity declaration. Traces to the QA test plan's benchmark-gate (cases
/// 27-32), stake-enforcement (33-40), HIPAA (41-45), on-chain-completeness
/// (46-48), and dual-role (49-52) sections.
contract ComputeNodeManifestTest is CoreProtocolTestBase {
    ComputeTierRegistry internal tiers;
    ComputeNodeManifest internal manifest;

    address internal computeNode = makeAddr("computeNode");

    uint8 internal constant NODE_TYPE_COMPUTE = 7; // Storage=0..EmailIP=6, Compute=7 (appended)

    function setUp() public {
        _deployCoreProtocol();

        tiers = ComputeTierRegistry(
            _deployProxy(address(new ComputeTierRegistry()), abi.encodeCall(ComputeTierRegistry.initialize, (cfg)))
        );
        manifest = ComputeNodeManifest(
            _deployProxy(address(new ComputeNodeManifest()), abi.encodeCall(ComputeNodeManifest.initialize, (cfg)))
        );

        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.COMPUTE_TIER_REGISTRY_ADDRESS, address(tiers));

        // Compute stake: 40,000 base + 5,000/vCPU (tokenomics-mvp.md §9.1, illustrative).
        cfg.setUint(ConfigKeys.registryStakeBase(NODE_TYPE_COMPUTE), 40_000e18);
        cfg.setUint(ConfigKeys.registryStakePerUnit(NODE_TYPE_COMPUTE), 5_000e18);

        cfg.setUint(ConfigKeys.COMPUTE_TIER_MAX_EPOCHS_BETWEEN_VERIFICATIONS, 7);
        cfg.setUint(ConfigKeys.COMPUTE_TIER_HARDWARE_TOLERANCE_BPS, 500);

        cfg.setUint(ConfigKeys.computeTierMinCores(3), 16);
        cfg.setUint(ConfigKeys.computeTierMinMips(3), 2_000);
        cfg.setUint(ConfigKeys.computeTierMinRamGB(3), 64);
        cfg.setUint(ConfigKeys.computeTierMinRamBandwidthGBs(3), 50);
        cfg.setUint(ConfigKeys.computeTierMinIops(3), 200_000);
        cfg.setUint(ConfigKeys.computeTierMaxRttMs(3), 2);

        cfg.setUint(ConfigKeys.computeTierMinCores(2), 8);
        cfg.setUint(ConfigKeys.computeTierMinMips(2), 1_000);
        cfg.setUint(ConfigKeys.computeTierMinRamGB(2), 32);
        cfg.setUint(ConfigKeys.computeTierMinRamBandwidthGBs(2), 20);
        cfg.setUint(ConfigKeys.computeTierMinIops(2), 50_000);
        cfg.setUint(ConfigKeys.computeTierMaxRttMs(2), 10);

        cfg.setUint(ConfigKeys.computeTierMinCores(1), 4);
        cfg.setUint(ConfigKeys.computeTierMinMips(1), 500);
        cfg.setUint(ConfigKeys.computeTierMinRamGB(1), 16);
        cfg.setUint(ConfigKeys.computeTierMinRamBandwidthGBs(1), 5);
        cfg.setUint(ConfigKeys.computeTierMinIops(1), 10_000);
        cfg.setUint(ConfigKeys.computeTierMaxRttMs(1), 30);
        vm.stopPrank();
    }

    function _registerCompute(address node, uint256 vcpu, uint256 stakeAmount) internal {
        _fund(node, stakeAmount);
        vm.prank(node);
        registry.register(INodeRegistry.NodeType.Compute, vcpu, "us-east", stakeAmount);
    }

    function _passBenchmarkAtTier3(address node) internal {
        ComputeTierRegistry.Measured memory measured =
            ComputeTierRegistry.Measured({mips: 500, ramBandwidthGBs: 5, iopsRandomRead: 10_000, peerRttMs: 30});
        address[] memory witnessSet = new address[](0);
        // minQuorumFor(0) returns 3, so use a pool/witness combo that
        // satisfies quorum trivially isn't possible with an empty set --
        // reuse a permissive witness source stub scoped to this test file.
        _MockWitness src = new _MockWitness();
        address w1 = makeAddr("w1");
        address w2 = makeAddr("w2");
        address w3 = makeAddr("w3");
        src.setFoundation(w1, "us-east");
        src.setFoundation(w2, "eu-west");
        src.setFoundation(w3, "eu-west");
        witnessSet = new address[](3);
        witnessSet[0] = w1;
        witnessSet[1] = w2;
        witnessSet[2] = w3;

        tiers.submitBenchmarkAttestation(node, 1, measured, 4, 16, 4, 16, witnessSet, 3, src);
    }

    function test_declareManifest_goldenPath_recordsAllFields() public {
        uint256 vcpu = 8;
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Compute, vcpu);
        _registerCompute(computeNode, vcpu, required);
        _passBenchmarkAtTier3(computeNode);

        uint8[] memory versions = new uint8[](1);
        versions[0] = 16;
        string[] memory extensions = new string[](1);
        extensions[0] = "pgvector";

        vm.prank(computeNode);
        manifest.declareManifest(versions, extensions, 10, uint32(vcpu), 32_000, true, false);

        ComputeNodeManifest.Manifest memory m = manifest.manifestOf(computeNode);
        assertEq(m.maxDatabases, 10);
        assertEq(m.maxVcpu, vcpu);
        assertEq(m.maxRamMB, 32_000);
        assertTrue(m.hipaaEligible);
        assertEq(uint8(m.tier), uint8(ComputeTierRegistry.ComputeTier.Tier3));
        assertTrue(manifest.isDeclared(computeNode));
        assertEq(manifest.extensionsOf(computeNode).length, 1);
        assertEq(manifest.extensionsOf(computeNode)[0], "pgvector");
    }

    function test_declareManifest_withoutBenchmark_reverts() public {
        uint256 vcpu = 8;
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Compute, vcpu);
        _registerCompute(computeNode, vcpu, required);

        uint8[] memory versions = new uint8[](1);
        versions[0] = 16;
        string[] memory extensions = new string[](0);

        vm.expectRevert(abi.encodeWithSelector(ComputeNodeManifest.BenchmarkBelowTier3.selector, computeNode));
        vm.prank(computeNode);
        manifest.declareManifest(versions, extensions, 10, uint32(vcpu), 32_000, false, false);
    }

    function test_declareManifest_withoutRegistration_reverts() public {
        _passBenchmarkAtTier3(computeNode);

        uint8[] memory versions = new uint8[](1);
        versions[0] = 16;
        string[] memory extensions = new string[](0);

        vm.expectRevert(abi.encodeWithSelector(ComputeNodeManifest.NodeNotRegistered.selector, computeNode));
        vm.prank(computeNode);
        manifest.declareManifest(versions, extensions, 10, 8, 32_000, false, false);
    }

    function test_register_insufficientStake_reverts() public {
        uint256 vcpu = 8;
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Compute, vcpu);
        _fund(computeNode, required);

        vm.prank(computeNode);
        vm.expectRevert();
        registry.register(INodeRegistry.NodeType.Compute, vcpu, "us-east", required - 1);
    }

    function test_declareManifest_noPgVersion_reverts() public {
        uint256 vcpu = 8;
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Compute, vcpu);
        _registerCompute(computeNode, vcpu, required);
        _passBenchmarkAtTier3(computeNode);

        uint8[] memory versions = new uint8[](0);
        string[] memory extensions = new string[](0);

        vm.expectRevert(abi.encodeWithSelector(ComputeNodeManifest.NoPgVersionDeclared.selector, computeNode));
        vm.prank(computeNode);
        manifest.declareManifest(versions, extensions, 10, uint32(vcpu), 32_000, false, false);
    }

    function test_declareManifest_hipaaOptOut_recordedIneligible() public {
        uint256 vcpu = 8;
        uint256 required = registry.minStakeFor(INodeRegistry.NodeType.Compute, vcpu);
        _registerCompute(computeNode, vcpu, required);
        _passBenchmarkAtTier3(computeNode);

        uint8[] memory versions = new uint8[](1);
        versions[0] = 16;
        string[] memory extensions = new string[](0);

        vm.prank(computeNode);
        manifest.declareManifest(versions, extensions, 10, uint32(vcpu), 32_000, false, false);

        assertFalse(manifest.isHipaaEligible(computeNode));
        assertTrue(manifest.isDeclared(computeNode));
    }

    function test_dualRoleOperator_separateStakesAndManifests() public {
        // Same operator (msg.sender key) registers a Storage node and a
        // Compute node on separate addresses -- StakingNodeRegistry keys by
        // address, so this asserts no accidental cross-contamination between
        // the two node identities' stake or manifest records.
        address storageNode = makeAddr("storageNodeSameOperator");
        uint256 storageRequired = registry.minStakeFor(INodeRegistry.NodeType.Storage, 1);
        _fund(storageNode, storageRequired);
        vm.prank(storageNode);
        registry.register(INodeRegistry.NodeType.Storage, 1, "us-east", storageRequired);

        uint256 vcpu = 8;
        uint256 computeRequired = registry.minStakeFor(INodeRegistry.NodeType.Compute, vcpu);
        _registerCompute(computeNode, vcpu, computeRequired);
        _passBenchmarkAtTier3(computeNode);

        uint8[] memory versions = new uint8[](1);
        versions[0] = 16;
        string[] memory extensions = new string[](0);
        vm.prank(computeNode);
        manifest.declareManifest(versions, extensions, 10, uint32(vcpu), 32_000, false, false);

        assertEq(registry.stakeOf(storageNode), storageRequired);
        assertEq(registry.stakeOf(computeNode), computeRequired);
        assertFalse(manifest.isDeclared(storageNode));
        assertTrue(manifest.isDeclared(computeNode));
    }
}

contract _MockWitness is IFoundationWitnessSource {
    mapping(address => bool) public foundationFlag;
    mapping(address => bytes32) public region;

    function setFoundation(address node, bytes32 nodeRegion) external {
        foundationFlag[node] = true;
        region[node] = nodeRegion;
    }

    function isFoundationNode(address node) external view returns (bool) {
        return foundationFlag[node];
    }

    function getFoundationRegion(address node) external view returns (bytes32) {
        return region[node];
    }
}
