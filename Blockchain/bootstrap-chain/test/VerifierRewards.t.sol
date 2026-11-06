// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {ScoringWeights} from "../src/ScoringWeights.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

/// @notice Issue #81: the Verifier Node Service is paid from the 8% verifier slice,
/// scored on uptime (40%), coverage (30%) and accuracy (30%).
contract VerifierRewardsTest is CoreProtocolTestBase {
    INodeRegistry.NodeType internal constant VERIFIER = INodeRegistry.NodeType.Verifier;
    INodeRegistry.NodeType internal constant STORAGE = INodeRegistry.NodeType.Storage;
    uint256 internal constant EMISSION = 1_000_000e18;
    uint256 internal constant SLICE = (EMISSION * 800) / 10_000;

    address internal v1 = makeAddr("verifier1");
    address internal v2 = makeAddr("verifier2");

    function setUp() public {
        _deployCoreProtocol();
        token.mint(address(rewards), 1_000_000_000e18);
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.registryStakeBase(5), 125e18);
    }

    function _register(address node, INodeRegistry.NodeType s, uint256 stake) internal {
        _fund(node, 10_000e18);
        vm.prank(node);
        registry.registerService(s, 0, "us-east", stake);
        vm.prank(verifierRole);
        registry.recordVerification(node, true, 0);
    }

    function _values(uint256 uptime, uint256 coverage, uint256 accuracy) internal pure returns (uint256[] memory v) {
        v = new uint256[](3);
        (v[0], v[1], v[2]) = (uptime, coverage, accuracy);
    }

    function _signService(address node, INodeRegistry.NodeType s, uint256 epoch, uint256[] memory values)
        internal
        view
        returns (bytes memory)
    {
        bytes32 digest = keccak256(
            abi.encodePacked(
                "\x19Ethereum Signed Message:\n32",
                keccak256(abi.encode(address(rewards), node, uint8(s), epoch, values, uint256(9_500)))
            )
        );
        (uint8 v, bytes32 r, bytes32 sig) = vm.sign(metricSignerPk, digest);
        return abi.encodePacked(r, sig, v);
    }

    function _reportVerifier(address node, uint256[] memory values) internal {
        rewards.submitServiceFactorMetrics(node, VERIFIER, 0, values, 9_500, _signService(node, VERIFIER, 0, values));
    }

    function _seal(address[] memory nodes) internal {
        vm.warp(rewards.epochFinalityDeadline(0) + 1);
        rewards.sealEpoch(0, nodes);
    }

    function test_VerifierTableIsUptimeCoverageAccuracy() public view {
        (uint16[] memory w, bool[] memory lower) = weights.weightsFor(5, 0);
        assertEq(w.length, 3);
        assertEq(w[0], 4_000);
        assertEq(w[1], 3_000);
        assertEq(w[2], 3_000);
        for (uint256 i; i < 3; ++i) assertFalse(lower[i], "every verifier factor is higher-is-better");
        assertEq(weights.factorCount(5), 3);
    }

    function test_SliceSplitByScore() public {
        _register(v1, VERIFIER, 125e18);
        _register(v2, VERIFIER, 125e18);
        _reportVerifier(v1, _values(10_000, 10_000, 10_000)); // best on everything: score 1.0
        _reportVerifier(v2, _values(10_000, 5_000, 5_000)); // 0.4 + 0.15 + 0.15 = 0.7
        address[] memory nodes = new address[](2);
        (nodes[0], nodes[1]) = (v1, v2);
        _seal(nodes);

        uint256 s1 = rewards.settlementOf(0, v1).score;
        uint256 s2 = rewards.settlementOf(0, v2).score;
        assertEq(s1, 1e18);
        assertEq(s2, 0.7e18);
        assertEq(rewards.payoutOf(0, v1), (SLICE * s1) / (s1 + s2));
        assertEq(rewards.payoutOf(0, v2), (SLICE * s2) / (s1 + s2));
        assertLe(rewards.payoutOf(0, v1) + rewards.payoutOf(0, v2), SLICE);
    }

    function test_RevertWhen_VerifierReportHasWrongFactorCount() public {
        _register(v1, VERIFIER, 125e18);
        uint256[] memory seven = new uint256[](7);
        bytes memory sig = _signService(v1, VERIFIER, 0, seven);
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.FactorCountMismatch.selector, 7, 3));
        rewards.submitServiceFactorMetrics(v1, VERIFIER, 0, seven, 9_500, sig);
    }

    function test_NoEligibleVerifierLeavesSliceUndistributed() public {
        _seal(new address[](0));
        assertEq(rewards.undistributed(), EMISSION, "nothing paid: capacity and verifier slices both retained");
    }

    function test_UnderStakedVerifierIsIneligible() public {
        _register(v1, VERIFIER, 125e18);
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.registryStakeBase(5), 125e18 + 1);
        _reportVerifier(v1, _values(10_000, 10_000, 10_000));
        address[] memory nodes = new address[](1);
        nodes[0] = v1;
        _seal(nodes);
        assertEq(uint8(rewards.settlementOf(0, v1).reason), uint8(RewardDistributor.SettlementReason.Ineligible));
        assertEq(rewards.undistributed(), EMISSION);
    }

    function test_StorageAndVerifierOnOneNodeArePaidFromSeparatePools() public {
        _fund(v1, 10_000e18);
        vm.startPrank(v1);
        registry.registerService(STORAGE, 0, "us-east", 100e18);
        registry.registerService(VERIFIER, 0, "us-east", 125e18);
        vm.stopPrank();
        vm.prank(verifierRole);
        registry.recordVerification(v1, true, 0);

        rewards.submitMetricReport(v1, 0, 100, 9_500, _signMetric(v1, 0, 100, 9_500)); // primary = Storage
        _reportVerifier(v1, _values(10_000, 10_000, 10_000));

        uint256 before = token.balanceOf(v1);
        address[] memory nodes = new address[](1);
        nodes[0] = v1;
        _seal(nodes);

        uint256 capacityPool = (EMISSION * 9_200) / 10_000;
        address verifierAccount = rewards.accountOf(v1, VERIFIER);
        assertEq(rewards.payoutOf(0, v1), capacityPool);
        assertEq(rewards.payoutOf(0, verifierAccount), SLICE);
        assertEq(token.balanceOf(v1), before + capacityPool + SLICE);
        assertEq(rewards.undistributed(), 0);
    }

    function test_NodeServiceTablesReinitializerIsIdempotent() public {
        vm.prank(timelockAddr);
        weights.initializeNodeServiceTables();
        assertEq(weights.historyLength(5), 1, "already seeded at initialize");
        vm.expectRevert();
        weights.initializeNodeServiceTables();
    }
}
