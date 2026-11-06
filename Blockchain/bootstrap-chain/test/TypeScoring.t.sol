// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {FactorScoring} from "../src/libraries/FactorScoring.sol";
import {ScoringWeights} from "../src/ScoringWeights.sol";
import {EpochController} from "../src/EpochController.sol";
import {MockProtocolConfig} from "../src/config/MockProtocolConfig.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {RewardDistributor} from "../src/RewardDistributor.sol";
import {TelemetryCheckpointRegistry} from "../src/TelemetryCheckpointRegistry.sol";
import {INodeRegistry} from "../src/interfaces/INodeRegistry.sol";
import {CoreProtocolTestBase} from "./helpers/CoreProtocolTestBase.sol";

/// @notice Issue #76: the shared normalize-then-weight pipeline.
contract FactorScoringTest is Test {
    uint256 internal constant WAD = 1e18;

    function _lower(uint256 n, uint256 lowerIndex) internal pure returns (bool[] memory l) {
        l = new bool[](n);
        if (lowerIndex < n) l[lowerIndex] = true;
    }

    function test_LowerIsBetterIsInverted() public pure {
        // Gherkin: OLTP query latencies 10ms and 50ms -> 1.0 and 0.2.
        assertEq(FactorScoring.normalize(10, 10, true), WAD);
        assertEq(FactorScoring.normalize(50, 10, true), 0.2e18);
        // Higher-is-better: best is 1.0, others are ratio-to-best.
        assertEq(FactorScoring.normalize(100, 100, false), WAD);
        assertEq(FactorScoring.normalize(25, 100, false), 0.25e18);
    }

    function test_ZeroMeansNotMeasured() public pure {
        assertEq(FactorScoring.normalize(0, 10, true), 0);
        assertEq(FactorScoring.normalize(0, 10, false), 0);
        assertEq(FactorScoring.normalize(5, 0, false), 0);
    }

    function test_BestsSkipZerosAndRespectDirection() public pure {
        uint256[][] memory raws = new uint256[][](3);
        raws[0] = _row(0, 40);
        raws[1] = _row(30, 10);
        raws[2] = _row(20, 0);
        bool[] memory lower = _lower(2, 1);
        uint256[] memory best = FactorScoring.bests(raws, lower);
        assertEq(best[0], 30); // max
        assertEq(best[1], 10); // smallest non-zero
    }

    function test_SoleNodeScoresOne() public pure {
        // Gherkin "Sole node in a type scores maximally" (Consensus: 4 factors, propagation latency lower).
        uint256[][] memory raws = new uint256[][](1);
        raws[0] = new uint256[](4);
        (raws[0][0], raws[0][1], raws[0][2], raws[0][3]) = (9_900, 77, 120, 3);
        bool[] memory lower = _lower(4, 2);
        uint16[] memory w = new uint16[](4);
        (w[0], w[1], w[2], w[3]) = (3_500, 3_000, 2_000, 1_500);
        assertEq(FactorScoring.score(raws[0], FactorScoring.bests(raws, lower), w, lower), WAD);
    }

    function test_StorageGoldenExample() public pure {
        // Three Storage nodes, every factor varying; expected values computed by hand:
        // factor order: retrieval ms L, uptime bps, latency ms L, io, bandwidth, available, used.
        uint256[][] memory raws = new uint256[][](3);
        raws[0] = _storage(100, 10_000, 40, 1_000, 500, 800, 200);
        raws[1] = _storage(200, 9_000, 20, 500, 1_000, 400, 400);
        raws[2] = _storage(400, 5_000, 80, 250, 250, 200, 100);
        bool[] memory lower = new bool[](7);
        lower[0] = true;
        lower[2] = true;
        uint16[] memory w = new uint16[](7);
        (w[0], w[1], w[2], w[3], w[4], w[5], w[6]) = (3_000, 2_000, 1_500, 1_000, 1_000, 800, 700);
        uint256[] memory best = FactorScoring.bests(raws, lower);

        // Node 0: 1.0*.30 + 1.0*.20 + 0.5*.15 + 1.0*.10 + 0.5*.10 + 1.0*.08 + 0.5*.07 = 0.84
        assertEq(FactorScoring.score(raws[0], best, w, lower), 0.84e18);
        // Node 1: 0.5*.30 + 0.9*.20 + 1.0*.15 + 0.5*.10 + 1.0*.10 + 0.5*.08 + 1.0*.07 = 0.74
        assertEq(FactorScoring.score(raws[1], best, w, lower), 0.74e18);
        // Node 2: .25*.30 + .5*.20 + .25*.15 + .25*.10 + .25*.10 + .25*.08 + .25*.07 = 0.3
        assertEq(FactorScoring.score(raws[2], best, w, lower), 0.3e18);
    }

    function testFuzz_Deterministic(uint64 a, uint64 b, uint64 c) public pure {
        uint256[][] memory raws = new uint256[][](2);
        raws[0] = _row(a, b);
        raws[1] = _row(b, c);
        bool[] memory lower = _lower(2, 0);
        uint16[] memory w = new uint16[](2);
        (w[0], w[1]) = (6_000, 4_000);
        uint256[] memory best = FactorScoring.bests(raws, lower);
        uint256 s = FactorScoring.score(raws[0], best, w, lower);
        assertEq(s, FactorScoring.score(raws[0], FactorScoring.bests(raws, lower), w, lower));
        assertLe(s, WAD);
    }

    function _row(uint256 x, uint256 y) internal pure returns (uint256[] memory r) {
        r = new uint256[](2);
        (r[0], r[1]) = (x, y);
    }

    function _storage(uint256 a, uint256 b, uint256 c, uint256 d, uint256 e, uint256 f, uint256 g)
        internal
        pure
        returns (uint256[] memory r)
    {
        r = new uint256[](7);
        (r[0], r[1], r[2], r[3], r[4], r[5], r[6]) = (a, b, c, d, e, f, g);
    }
}

/// @notice Issue #76: weight tables, invariants and prospective governance changes.
contract ScoringWeightsTest is Test {
    MockProtocolConfig internal cfg;
    ScoringWeights internal w;
    EpochController internal epochs;
    address internal configOwner = makeAddr("configOwner");
    address internal timelock = makeAddr("timelock");

    function setUp() public {
        vm.warp(1_750_000_000);
        cfg = new MockProtocolConfig(configOwner);
        epochs = new EpochController(cfg, uint64(block.timestamp), 1 days, 15 minutes);
        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.GOVERNANCE_TIMELOCK, timelock);
        cfg.setAddress(ConfigKeys.EPOCH_CONTROLLER, address(epochs));
        vm.stopPrank();
        w = ScoringWeights(address(new ERC1967Proxy(address(new ScoringWeights()), abi.encodeCall(ScoringWeights.initialize, (cfg)))));
    }

    function test_SeededTablesMatchSpecAndSumToOne() public view {
        uint256[5] memory counts = [uint256(7), 5, 5, 4, 5];
        for (uint8 t; t < 5; ++t) {
            (uint16[] memory weights, bool[] memory lower) = w.weightsFor(t, 0);
            assertEq(weights.length, counts[t]);
            assertEq(lower.length, counts[t]);
            uint256 sum;
            for (uint256 i; i < weights.length; ++i) sum += weights[i];
            assertEq(sum, 10_000);
        }
        (uint16[] memory storageW, bool[] memory storageL) = w.weightsFor(0, 0);
        assertEq(storageW[0], 3_000);
        assertEq(storageW[6], 700);
        assertTrue(storageL[0] && storageL[2] && !storageL[1]);
        (, bool[] memory consensusL) = w.weightsFor(3, 0);
        assertTrue(consensusL[2]);
    }

    function test_WeightChangeIsProspective() public {
        // Gherkin: epoch 100 scored with the original table; the change applies from 101.
        vm.warp(epochs.epochStart(100) + 1 hours);
        uint16[] memory next = new uint16[](7);
        (next[0], next[1], next[2], next[3], next[4], next[5], next[6]) = (2_000, 3_000, 1_500, 1_000, 1_000, 800, 700);
        vm.prank(timelock);
        w.setWeights(0, next);

        (uint16[] memory e100,) = w.weightsFor(0, 100);
        (uint16[] memory e101,) = w.weightsFor(0, 101);
        (uint16[] memory e0,) = w.weightsFor(0, 0);
        assertEq(e100[0], 3_000);
        assertEq(e0[0], 3_000);
        assertEq(e101[0], 2_000);
    }

    function test_PendingChangeIsReplaced() public {
        uint16[] memory a = new uint16[](4);
        (a[0], a[1], a[2], a[3]) = (2_500, 2_500, 2_500, 2_500);
        uint16[] memory b = new uint16[](4);
        (b[0], b[1], b[2], b[3]) = (4_000, 2_000, 2_000, 2_000);
        vm.startPrank(timelock);
        w.setWeights(3, a);
        w.setWeights(3, b);
        vm.stopPrank();
        assertEq(w.historyLength(3), 2);
        (uint16[] memory e1,) = w.weightsFor(3, 1);
        assertEq(e1[0], 4_000);
    }

    function test_RevertWhen_InvalidOrUnauthorized() public {
        uint16[] memory bad = new uint16[](5);
        (bad[0], bad[1], bad[2], bad[3], bad[4]) = (2_000, 2_000, 2_000, 2_000, 1_999); // 99.99%
        vm.prank(timelock);
        vm.expectRevert(abi.encodeWithSelector(ScoringWeights.InvalidWeights.selector, 1));
        w.setWeights(1, bad);

        uint16[] memory wrongLength = new uint16[](4);
        (wrongLength[0], wrongLength[1], wrongLength[2], wrongLength[3]) = (2_500, 2_500, 2_500, 2_500);
        vm.prank(timelock);
        vm.expectRevert(abi.encodeWithSelector(ScoringWeights.InvalidWeights.selector, 1));
        w.setWeights(1, wrongLength);

        vm.expectRevert();
        w.setWeights(3, wrongLength); // not the timelock

        vm.expectRevert(abi.encodeWithSelector(ScoringWeights.UnknownNodeType.selector, 6));
        w.factorCount(6); // Email IP Address has no table: it never earns
    }
}

/// @notice Stand-in TelemetryCheckpointRegistry: records, finalization and the deadline are set directly.
contract MockTelemetryRecords {
    mapping(uint256 => mapping(address => TelemetryCheckpointRegistry.AggregatedMetricRecord)) internal _records;
    mapping(uint256 => bool) public recordsFinalized;
    mapping(uint256 => uint256) public recordCount;
    mapping(uint256 => uint256) public telemetryDeadline;

    function setRecord(uint256 epoch, address node, TelemetryCheckpointRegistry.AggregatedMetricRecord memory r) external {
        if (!_records[epoch][node].exists) recordCount[epoch] += 1;
        r.exists = true;
        _records[epoch][node] = r;
    }

    function finalize(uint256 epoch) external {
        recordsFinalized[epoch] = true;
    }

    function setDeadline(uint256 epoch, uint256 at) external {
        telemetryDeadline[epoch] = at;
    }

    function getEpochMetricRecord(address node, uint256 epoch)
        external
        view
        returns (TelemetryCheckpointRegistry.AggregatedMetricRecord memory)
    {
        return _records[epoch][node];
    }
}

/// @notice Issue #76 + #158: settlement scores storage nodes from finalized telemetry records.
contract TelemetrySettlementTest is CoreProtocolTestBase {
    MockTelemetryRecords internal telemetry;
    address internal nodeA = makeAddr("nodeA");
    address internal nodeB = makeAddr("nodeB");

    function setUp() public {
        _deployCoreProtocol();
        token.mint(address(rewards), 10_000_000e18); // flat REWARD_EMISSION_PER_EPOCH is paid from the distributor's balance
        telemetry = new MockTelemetryRecords();
        telemetry.setDeadline(0, epochs.finalityDeadline(0) + 30 minutes);
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.REWARD_TELEMETRY_REGISTRY, address(telemetry));

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

    function _record(uint16 compliance, uint32 latencyMs, uint64 throughput)
        internal
        pure
        returns (TelemetryCheckpointRegistry.AggregatedMetricRecord memory r)
    {
        r.heartbeatComplianceBps = compliance;
        r.heartbeatsReceived = compliance == 0 ? 0 : 1;
        r.heartbeatsExpected = 288;
        r.eligible = compliance > 0;
        r.selfReported.latencyMs = latencyMs;
        r.selfReported.retrievalMs = latencyMs * 2;
        r.selfReported.readThroughputKBps = throughput;
        r.availableBytes = 1_000;
        r.usedBytes = 500;
    }

    function _both() internal view returns (address[] memory n) {
        n = new address[](2);
        (n[0], n[1]) = (nodeA, nodeB);
    }

    function test_SettlesFromRecordsOnceFinalized() public {
        telemetry.setRecord(0, nodeA, _record(10_000, 20, 1_000));
        telemetry.setRecord(0, nodeB, _record(10_000, 40, 500));
        vm.warp(epochs.finalityDeadline(0) + 1);
        assertEq(uint8(rewards.epochSource(0)), uint8(RewardDistributor.EpochSource.Pending));
        assertFalse(rewards.canSeal(0));
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.TelemetryRecordsPending.selector, 0));
        rewards.sealEpoch(0, _both());

        telemetry.finalize(0);
        assertTrue(rewards.canSeal(0));
        uint256 beforeA = token.balanceOf(nodeA);
        uint256 beforeB = token.balanceOf(nodeB);
        rewards.sealEpoch(0, _both());

        // A leads retrieval, latency and throughput; uptime and capacity tie; bandwidth is
        // unmeasured for both, so its 0.10 is never earned.
        // A: .30 + .20 + .15 + .10 + 0 + .08 + .07 = 0.90
        // B: .5*.30 + .20 + .5*.15 + .5*.10 + 0 + .08 + .07 = 0.625
        assertEq(rewards.settlementOf(0, nodeA).score, 0.9e18);
        assertEq(rewards.settlementOf(0, nodeB).score, 0.625e18);
        uint256 paidA = token.balanceOf(nodeA) - beforeA;
        uint256 paidB = token.balanceOf(nodeB) - beforeB;
        assertApproxEqRel(paidA * 625, paidB * 900, 0.001e18);
    }

    function test_RecordComplianceDrivesTheHeartbeatGate() public {
        telemetry.setRecord(0, nodeA, _record(8_000, 20, 1_000)); // 80% < 90% gate: held in reserve
        telemetry.finalize(0);
        vm.warp(epochs.finalityDeadline(0) + 1);
        address[] memory one = new address[](1);
        one[0] = nodeA;
        rewards.sealEpoch(0, one);
        assertGt(rewards.reserveBalanceOf(nodeA), 0);
        assertEq(rewards.metricOf(0, nodeA).heartbeatBps, 8_000);
    }

    function test_RevertWhen_RecordNodeOmitted() public {
        telemetry.setRecord(0, nodeA, _record(10_000, 20, 1_000));
        telemetry.setRecord(0, nodeB, _record(10_000, 40, 500));
        telemetry.finalize(0);
        vm.warp(epochs.finalityDeadline(0) + 1);
        address[] memory one = new address[](1);
        one[0] = nodeA;
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.IncompleteNodeSet.selector, 0, 1, 2));
        rewards.sealEpoch(0, one);
    }

    function test_FallsBackToSignedReportsAfterTheWait() public {
        rewards.submitMetricReport(nodeA, 0, 100, 9_500, _signMetric(nodeA, 0, 100, 9_500));
        vm.warp(telemetry.telemetryDeadline(0) + 24 hours);
        assertEq(uint8(rewards.epochSource(0)), uint8(RewardDistributor.EpochSource.Pending));
        vm.warp(telemetry.telemetryDeadline(0) + 24 hours + 1);
        assertEq(uint8(rewards.epochSource(0)), uint8(RewardDistributor.EpochSource.Signed));
        address[] memory one = new address[](1);
        one[0] = nodeA;
        uint256 before = token.balanceOf(nodeA);
        rewards.sealEpoch(0, one);
        assertGt(token.balanceOf(nodeA) - before, 0);
    }

    function test_RevertWhen_SignedFactorCountMismatchesType() public {
        uint256[] memory five = new uint256[](5);
        vm.expectRevert(abi.encodeWithSelector(RewardDistributor.FactorCountMismatch.selector, 5, 7));
        rewards.submitFactorMetrics(nodeA, 0, five, 9_500, _signFactorMetrics(nodeA, 0, five, 9_500));
    }
}
