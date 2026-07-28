// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {Vm} from "forge-std/Vm.sol";
import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {MockProtocolConfig} from "../src/config/MockProtocolConfig.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {ComputeTierRegistry, IFoundationWitnessSource} from "../src/ComputeTierRegistry.sol";

/// @notice Minimal witness-pool double, interface-compatible with
/// `FoundationRegistry`'s `isFoundationNode`/`getFoundationRegion` surface,
/// so this suite can drive quorum/diversity/self-verification scenarios
/// without standing up the full core-protocol stack `FoundationRegistry`
/// itself depends on.
contract MockWitnessSource is IFoundationWitnessSource {
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

/// @notice Test suite for issue #89: verifier-witnessed compute-node
/// benchmarking and tier classification. Traces to the QA test plan's Group
/// 1 (classification matrix), Group 2 (witness quorum/diversity/self-
/// verification), Group 4 (downgrade-on-reverification), and Group 6
/// (declared-vs-observed hardware tolerance).
contract ComputeTierRegistryTest is Test {
    ComputeTierRegistry internal tiers;
    MockProtocolConfig internal cfg;
    MockWitnessSource internal witnesses;

    address internal configOwner = makeAddr("configOwner");
    address internal node = makeAddr("node1");

    bytes32 internal constant REGION_A = "us-east";
    bytes32 internal constant REGION_B = "eu-west";

    // Tier1 thresholds per compute-nodes.md: 16 cores, 2000 MIPS, 64GB RAM, 50GB/s, 200K IOPS, 2ms RTT.
    // Tier2: 8 cores, 1000 MIPS, 32GB RAM, 20GB/s, 50K IOPS, 10ms RTT.
    // Tier3: 4 cores, 500 MIPS, 16GB RAM, 5GB/s, 10K IOPS, 30ms RTT.
    function setUp() public {
        cfg = new MockProtocolConfig(configOwner);
        witnesses = new MockWitnessSource();

        tiers = ComputeTierRegistry(
            _deployProxy(address(new ComputeTierRegistry()), abi.encodeCall(ComputeTierRegistry.initialize, (cfg)))
        );

        vm.startPrank(configOwner);
        cfg.setUint(ConfigKeys.COMPUTE_TIER_MAX_EPOCHS_BETWEEN_VERIFICATIONS, 7);
        cfg.setUint(ConfigKeys.COMPUTE_TIER_HARDWARE_TOLERANCE_BPS, 500); // 5%

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

    function _deployProxy(address implementation, bytes memory initData) internal returns (address) {
        ERC1967Proxy proxy = new ERC1967Proxy(implementation, initData);
        return address(proxy);
    }

    function _quorumOf3() internal returns (address[] memory set) {
        set = new address[](3);
        set[0] = makeAddr("w1");
        set[1] = makeAddr("w2");
        set[2] = makeAddr("w3");
        witnesses.setFoundation(set[0], REGION_A);
        witnesses.setFoundation(set[1], REGION_B);
        witnesses.setFoundation(set[2], REGION_B);
    }

    function _tier1Measured() internal pure returns (ComputeTierRegistry.Measured memory) {
        return ComputeTierRegistry.Measured({mips: 2_500, ramBandwidthGBs: 60, iopsRandomRead: 250_000, peerRttMs: 1});
    }

    // --- Group 1: classification matrix (AC1-AC3) ---

    function test_classifyTier_meetsTier1_allDimensions() public {
        address[] memory w = _quorumOf3();
        ComputeTier1WitnessAssert(w);
    }

    function ComputeTier1WitnessAssert(address[] memory w) internal {
        ComputeTierRegistry.ComputeTier result = tiers.submitBenchmarkAttestation(
            node, 1, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses
        );
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Tier1));
        assertEq(uint8(tiers.currentTierOf(node)), uint8(ComputeTierRegistry.ComputeTier.Tier1));
    }

    function test_classifyTier_lowestQualifyingMetricGoverns() public {
        address[] memory w = _quorumOf3();
        // Great IOPS/RTT, but MIPS and RAM only clear Tier 2.
        ComputeTierRegistry.Measured memory measured =
            ComputeTierRegistry.Measured({mips: 1_200, ramBandwidthGBs: 60, iopsRandomRead: 250_000, peerRttMs: 1});
        ComputeTierRegistry.ComputeTier result =
            tiers.submitBenchmarkAttestation(node, 1, measured, 16, 40, 16, 40, w, 3, witnesses);
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Tier2));
    }

    function test_classifyTier_boundaryExactMinusOne_missesHigherTier() public {
        address[] memory w = _quorumOf3();
        ComputeTierRegistry.Measured memory measured =
            ComputeTierRegistry.Measured({mips: 2_000, ramBandwidthGBs: 50, iopsRandomRead: 199_999, peerRttMs: 2});
        ComputeTierRegistry.ComputeTier result =
            tiers.submitBenchmarkAttestation(node, 1, measured, 16, 64, 16, 64, w, 3, witnesses);
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Tier2));
    }

    function test_classifyTier_belowTier3Minimum_rejectsRegistration() public {
        address[] memory w = _quorumOf3();
        ComputeTierRegistry.Measured memory measured =
            ComputeTierRegistry.Measured({mips: 600, ramBandwidthGBs: 4, iopsRandomRead: 15_000, peerRttMs: 25});
        ComputeTierRegistry.ComputeTier result =
            tiers.submitBenchmarkAttestation(node, 1, measured, 4, 16, 4, 16, w, 3, witnesses);
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Rejected));
        assertEq(uint8(tiers.currentTierOf(node)), uint8(ComputeTierRegistry.ComputeTier.Rejected));
    }

    function test_classifyTier_coreCountGatesTier_evenWithGreatMeasuredValues() public {
        address[] memory w = _quorumOf3();
        // Meets every measured threshold for Tier 1, but declares only 8 cores (Tier 2 core count).
        ComputeTierRegistry.ComputeTier result =
            tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 8, 64, 8, 64, w, 3, witnesses);
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Tier2));
    }

    function test_classifyTier_inclusiveBoundarySemantics() public {
        address[] memory w = _quorumOf3();
        ComputeTierRegistry.Measured memory measured =
            ComputeTierRegistry.Measured({mips: 1_000, ramBandwidthGBs: 20, iopsRandomRead: 50_000, peerRttMs: 10});
        ComputeTierRegistry.ComputeTier result =
            tiers.submitBenchmarkAttestation(node, 1, measured, 8, 32, 8, 32, w, 3, witnesses);
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Tier2));
    }

    // --- Group 2: witness quorum / diversity / self-verification (AC4) ---

    function test_submitBenchmark_belowQuorumForBracket_reverts() public {
        address[] memory w = new address[](2);
        w[0] = makeAddr("w1");
        w[1] = makeAddr("w2");
        witnesses.setFoundation(w[0], REGION_A);
        witnesses.setFoundation(w[1], REGION_B);

        vm.expectRevert(abi.encodeWithSelector(ComputeTierRegistry.InsufficientWitnessQuorum.selector, 2, 3));
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 64, w, 5, witnesses);
    }

    function test_submitBenchmark_largerPoolBracket_requiresHigherQuorum() public {
        address[] memory w = new address[](4);
        for (uint256 i = 0; i < 4; i++) {
            w[i] = makeAddr(string.concat("w", vm.toString(i)));
            witnesses.setFoundation(w[i], i % 2 == 0 ? REGION_A : REGION_B);
        }
        // Pool size 15 -> bracket 11-30 -> requires 5, only 4 provided.
        vm.expectRevert(abi.encodeWithSelector(ComputeTierRegistry.InsufficientWitnessQuorum.selector, 4, 5));
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 64, w, 15, witnesses);
    }

    function test_submitBenchmark_noGeographicDiversity_reverts() public {
        address[] memory w = new address[](3);
        w[0] = makeAddr("w1");
        w[1] = makeAddr("w2");
        w[2] = makeAddr("w3");
        witnesses.setFoundation(w[0], REGION_A);
        witnesses.setFoundation(w[1], REGION_A);
        witnesses.setFoundation(w[2], REGION_A);

        vm.expectRevert(ComputeTierRegistry.InsufficientGeographicDiversity.selector);
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses);
    }

    function test_submitBenchmark_witnessSharesNodeIdentity_reverts() public {
        address[] memory w = new address[](3);
        w[0] = node;
        w[1] = makeAddr("w2");
        w[2] = makeAddr("w3");
        witnesses.setFoundation(w[1], REGION_A);
        witnesses.setFoundation(w[2], REGION_B);

        vm.expectRevert(abi.encodeWithSelector(ComputeTierRegistry.SelfVerificationNotAllowed.selector, node));
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses);
    }

    function test_submitBenchmark_nonFoundationWitness_reverts() public {
        address[] memory w = new address[](3);
        w[0] = makeAddr("w1");
        w[1] = makeAddr("w2");
        w[2] = makeAddr("notFoundation");
        witnesses.setFoundation(w[0], REGION_A);
        witnesses.setFoundation(w[1], REGION_B);
        // w[2] deliberately left unregistered.

        vm.expectRevert(abi.encodeWithSelector(ComputeTierRegistry.WitnessNotFoundationVerifier.selector, w[2]));
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses);
    }

    function test_submitBenchmark_zeroWitnesses_reverts() public {
        address[] memory w = new address[](0);
        vm.expectRevert(abi.encodeWithSelector(ComputeTierRegistry.InsufficientWitnessQuorum.selector, 0, 3));
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 64, w, 5, witnesses);
    }

    // --- Group 4: periodic re-verification & downgrade (AC5-AC6) ---

    function test_reverification_downgradesToMeasuredTier_andEmitsEvidence() public {
        address[] memory w = _quorumOf3();
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses);
        assertEq(uint8(tiers.currentTierOf(node)), uint8(ComputeTierRegistry.ComputeTier.Tier1));

        ComputeTierRegistry.Measured memory degraded =
            ComputeTierRegistry.Measured({mips: 2_500, ramBandwidthGBs: 60, iopsRandomRead: 60_000, peerRttMs: 1});

        vm.recordLogs();
        tiers.submitBenchmarkAttestation(node, 7, degraded, 16, 64, 16, 64, w, 3, witnesses);
        assertEq(uint8(tiers.currentTierOf(node)), uint8(ComputeTierRegistry.ComputeTier.Tier2));

        Vm.Log[] memory entries = vm.getRecordedLogs();
        bool sawDowngrade = false;
        for (uint256 i = 0; i < entries.length; i++) {
            if (entries[i].topics[0] == ComputeTierRegistry.TierDowngraded.selector) sawDowngrade = true;
        }
        assertTrue(sawDowngrade, "expected TierDowngraded event");
    }

    function test_reverification_stillMeetsTier_noDowngradeEvent() public {
        address[] memory w = _quorumOf3();
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses);
        tiers.submitBenchmarkAttestation(node, 7, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses);
        assertEq(uint8(tiers.currentTierOf(node)), uint8(ComputeTierRegistry.ComputeTier.Tier1));
        assertEq(tiers.lastVerifiedEpochOf(node), 7);
    }

    function test_reverification_belowTier3_flagsRejectedNotDowngradedToNonexistentTier() public {
        address[] memory w = _quorumOf3();
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses);

        ComputeTierRegistry.Measured memory failing =
            ComputeTierRegistry.Measured({mips: 100, ramBandwidthGBs: 1, iopsRandomRead: 1_000, peerRttMs: 50});
        tiers.submitBenchmarkAttestation(node, 7, failing, 16, 64, 16, 64, w, 3, witnesses);
        assertEq(uint8(tiers.currentTierOf(node)), uint8(ComputeTierRegistry.ComputeTier.Rejected));
    }

    function test_isDueForReverification_neverClassified_isDue() public view {
        assertTrue(tiers.isDueForReverification(node, 0));
    }

    function test_isDueForReverification_atSevenEpochs_isDue() public {
        address[] memory w = _quorumOf3();
        tiers.submitBenchmarkAttestation(node, 0, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses);
        assertFalse(tiers.isDueForReverification(node, 6));
        assertTrue(tiers.isDueForReverification(node, 7));
    }

    // --- Group 6: declared-vs-observed hardware tolerance (AC8) ---

    function test_hardwareMismatch_beyondTolerance_treatedAsFailed() public {
        address[] memory w = _quorumOf3();
        // Declares 32 cores, witness probe observes 8 -- wildly beyond the 5% tolerance.
        ComputeTierRegistry.ComputeTier result =
            tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 32, 64, 8, 64, w, 3, witnesses);
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Rejected));
        assertEq(uint8(tiers.currentTierOf(node)), uint8(ComputeTierRegistry.ComputeTier.Rejected));
    }

    function test_hardwareMismatch_withinTolerance_accepted() public {
        address[] memory w = _quorumOf3();
        // Declared 64GB RAM vs observed 66GB is a 3.1% diff, within the configured 5% tolerance.
        ComputeTierRegistry.ComputeTier result =
            tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 66, w, 3, witnesses);
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Tier1));
    }

    function test_hardwareMismatch_ramBeyondTolerance_treatedAsFailed() public {
        address[] memory w = _quorumOf3();
        ComputeTierRegistry.ComputeTier result =
            tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 16, 20, w, 3, witnesses);
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Rejected));
    }

    function test_hardwareMismatch_resolvedOnResubmit_canBeReclassified() public {
        address[] memory w = _quorumOf3();
        tiers.submitBenchmarkAttestation(node, 1, _tier1Measured(), 16, 64, 8, 64, w, 3, witnesses);
        assertEq(uint8(tiers.currentTierOf(node)), uint8(ComputeTierRegistry.ComputeTier.Rejected));

        ComputeTierRegistry.ComputeTier result =
            tiers.submitBenchmarkAttestation(node, 2, _tier1Measured(), 16, 64, 16, 64, w, 3, witnesses);
        assertEq(uint8(result), uint8(ComputeTierRegistry.ComputeTier.Tier1));
    }

    // --- minQuorumFor bracket boundaries ---

    function test_minQuorumFor_brackets() public view {
        assertEq(tiers.minQuorumFor(3), 3);
        assertEq(tiers.minQuorumFor(10), 3);
        assertEq(tiers.minQuorumFor(11), 5);
        assertEq(tiers.minQuorumFor(30), 5);
        assertEq(tiers.minQuorumFor(31), 7);
        assertEq(tiers.minQuorumFor(100), 7);
        assertEq(tiers.minQuorumFor(101), 10);
    }
}
