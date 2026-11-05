// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {MockProtocolConfig} from "../src/config/MockProtocolConfig.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";
import {ReliabilityRegistry} from "../src/ReliabilityRegistry.sol";
import {BonusNormalization} from "../src/libraries/BonusNormalization.sol";

contract ReliabilityRegistryTest is Test {
    ReliabilityRegistry internal rel;
    MockProtocolConfig internal cfg;
    address internal configOwner = makeAddr("configOwner");
    address internal timelock = makeAddr("timelock");
    address internal challenger = makeAddr("challenger");
    address internal finalizer = makeAddr("distributor");
    address internal node = makeAddr("node");

    uint256 internal constant POR = 200; // 0.02
    uint256 internal constant PROBE = 100; // 0.01
    uint256 internal constant TIMEOUT = 50; // 0.005
    uint256 internal constant HB_PER_PCT = 20; // 0.002 per missed point
    uint256 internal constant RECOVERY = 50; // 0.005 per clean epoch

    function setUp() public {
        cfg = new MockProtocolConfig(configOwner);
        rel = ReliabilityRegistry(
            address(new ERC1967Proxy(address(new ReliabilityRegistry()), abi.encodeCall(ReliabilityRegistry.initialize, (cfg))))
        );
        vm.startPrank(configOwner);
        cfg.setAddress(ConfigKeys.GOVERNANCE_TIMELOCK, timelock);
        cfg.setAddress(ConfigKeys.RELIABILITY_CHALLENGER, challenger);
        cfg.setAddress(ConfigKeys.RELIABILITY_FINALIZER, finalizer);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_POR_BPS, POR);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_PROBE_BPS, PROBE);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_TIMEOUT_BPS, TIMEOUT);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_HEARTBEAT_BPS_PER_PCT, HB_PER_PCT);
        cfg.setUint(ConfigKeys.RELIABILITY_RECOVERY_BPS, RECOVERY);
        cfg.setUint(ConfigKeys.RELIABILITY_BONUS_WINDOW, 30);
        cfg.setUint(ConfigKeys.REWARD_RELIABILITY_BONUS_THRESHOLD_BPS, 9_800);
        vm.stopPrank();
    }

    function _fail(ReliabilityRegistry.ChallengeKind kind, uint256 epoch, uint256 nonce) internal {
        vm.prank(challenger);
        if (kind == ReliabilityRegistry.ChallengeKind.Timeout) rel.recordTimeout(node, epoch, bytes32(nonce), nonce);
        else rel.recordChallengeOutcome(node, epoch, kind, false, bytes32(nonce), nonce);
    }

    function _finalize(uint256 epoch, uint256 hb) internal returns (uint256 score, bool bonus) {
        vm.prank(finalizer);
        return rel.finalizeEpoch(node, epoch, hb);
    }

    function _cleanEpochs(uint256 from, uint256 count) internal {
        for (uint256 e = from; e < from + count; ++e) {
            _finalize(e, 10_000);
        }
    }

    // --- Baseline & degradation ---

    function test_NewNodeStartsAtOne() public view {
        assertEq(rel.currentScoreBps(node), 10_000);
    }

    function test_FailedPorReducesByFixedDelta() public {
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 1);
        assertEq(rel.currentScoreBps(node), 10_000 - POR);
    }

    function test_FailedProbeReducesByFixedDelta() public {
        _fail(ReliabilityRegistry.ChallengeKind.ResourceProbe, 0, 1);
        assertEq(rel.currentScoreBps(node), 10_000 - PROBE);
    }

    function test_TimeoutReducesByFixedDelta() public {
        _fail(ReliabilityRegistry.ChallengeKind.Timeout, 0, 1);
        assertEq(rel.currentScoreBps(node), 10_000 - TIMEOUT);
    }

    function test_PassedChallengeHasNoEffect() public {
        vm.prank(challenger);
        rel.recordChallengeOutcome(node, 0, ReliabilityRegistry.ChallengeKind.Por, true, bytes32(0), 1);
        assertEq(rel.currentScoreBps(node), 10_000);
    }

    function test_MissedHeartbeatsDegradePerFullPoint() public {
        (uint256 score,) = _finalize(0, 9_550); // 4.5% missed -> 4 full points
        assertEq(score, 10_000 - 4 * HB_PER_PCT);
    }

    function test_ScoreClampsAtZero() public {
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.RELIABILITY_DELTA_POR_BPS, 6_000);
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 1);
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 2);
        assertEq(rel.currentScoreBps(node), 0);
        (uint256 score,) = _finalize(0, 0);
        assertEq(score, 0);
    }

    function test_RevertWhen_ReplayedOutcome() public {
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 7);
        vm.prank(challenger);
        vm.expectRevert();
        rel.recordChallengeOutcome(node, 0, ReliabilityRegistry.ChallengeKind.Por, false, bytes32(0), 7);
        assertEq(rel.currentScoreBps(node), 10_000 - POR);
    }

    function test_SameNonceDifferentKindIsDistinct() public {
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 7);
        _fail(ReliabilityRegistry.ChallengeKind.ResourceProbe, 0, 7);
        assertEq(rel.currentScoreBps(node), 10_000 - POR - PROBE);
    }

    function test_RevertWhen_UnauthorizedChallenger() public {
        vm.expectRevert(abi.encodeWithSelector(ReliabilityRegistry.NotChallenger.selector, address(this)));
        rel.recordChallengeOutcome(node, 0, ReliabilityRegistry.ChallengeKind.Por, false, bytes32(0), 1);
    }

    function test_RevertWhen_UnauthorizedFinalizer() public {
        vm.expectRevert(abi.encodeWithSelector(ReliabilityRegistry.NotFinalizer.selector, address(this)));
        rel.finalizeEpoch(node, 0, 10_000);
    }

    function test_RevertWhen_TimeoutViaChallengeEntry() public {
        vm.prank(challenger);
        vm.expectRevert(ReliabilityRegistry.TimeoutViaChallenge.selector);
        rel.recordChallengeOutcome(node, 0, ReliabilityRegistry.ChallengeKind.Timeout, false, bytes32(0), 1);
    }

    function test_RevertWhen_RecordingIntoFinalizedEpoch() public {
        _finalize(3, 10_000);
        vm.prank(challenger);
        vm.expectRevert(abi.encodeWithSelector(ReliabilityRegistry.EpochAlreadyFinalized.selector, node, 3));
        rel.recordTimeout(node, 3, bytes32(0), 1);
    }

    function test_RevertWhen_FinalizeOutOfOrder() public {
        _finalize(5, 10_000);
        vm.prank(finalizer);
        vm.expectRevert(abi.encodeWithSelector(ReliabilityRegistry.OutOfOrderEpoch.selector, node, 5, 5));
        rel.finalizeEpoch(node, 5, 10_000);
    }

    function test_RevertWhen_HeartbeatAboveOne() public {
        vm.prank(finalizer);
        vm.expectRevert(abi.encodeWithSelector(ReliabilityRegistry.InvalidHeartbeat.selector, 10_001));
        rel.finalizeEpoch(node, 0, 10_001);
    }

    function test_RevertWhen_QueryUnfinalized() public {
        vm.expectRevert(abi.encodeWithSelector(ReliabilityRegistry.EpochNotFinalized.selector, node, 0));
        rel.reliabilityOf(node, 0);
    }

    // --- Recovery ---

    function test_GradualRecoveryOnCleanEpochs() public {
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 1);
        (uint256 s0,) = _finalize(0, 10_000); // dirty epoch: no recovery
        assertEq(s0, 10_000 - POR);
        (uint256 s1,) = _finalize(1, 10_000);
        assertEq(s1, 10_000 - POR + RECOVERY);
        _cleanEpochs(2, 10);
        assertEq(rel.currentScoreBps(node), 10_000); // capped at 1.0
    }

    function test_HeartbeatMissBlocksRecovery() public {
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 1);
        _finalize(0, 10_000);
        (uint256 s1,) = _finalize(1, 9_900); // 1 point missed: ding, no recovery
        assertEq(s1, 10_000 - POR - HB_PER_PCT);
    }

    // --- Trailing-window bonus ---

    function test_BonusAfterExactly30HighEpochs() public {
        _cleanEpochs(0, 29);
        (, bool b29) = _finalize(29, 10_000);
        assertTrue(b29);
        (, bool b28) = rel.reliabilityOf(node, 28);
        assertFalse(b28);
    }

    function test_NewNodeWithShortHistoryNotEligible() public {
        _cleanEpochs(0, 10);
        (, bool b) = rel.reliabilityOf(node, 9);
        assertFalse(b);
        assertEq(rel.highStreakOf(node), 10);
    }

    function test_ExactlyAtThresholdCounts() public {
        // 9_800 exactly: one POR fail (200) from 1.0.
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 1);
        (uint256 s,) = _finalize(0, 10_000);
        assertEq(s, 9_800);
        assertEq(rel.highStreakOf(node), 1);
    }

    function test_DipBelowThresholdResetsWindow() public {
        _cleanEpochs(0, 20);
        _fail(ReliabilityRegistry.ChallengeKind.Por, 20, 1);
        _fail(ReliabilityRegistry.ChallengeKind.Timeout, 20, 2); // 9_750 < 9_800
        (, bool b20) = _finalize(20, 10_000);
        assertFalse(b20);
        assertEq(rel.highStreakOf(node), 0);
        // Recovers above threshold next epoch, then needs a fresh 30.
        _cleanEpochs(21, 29);
        (, bool b49) = rel.reliabilityOf(node, 49);
        assertFalse(b49);
        (, bool b50) = _finalize(50, 10_000);
        assertTrue(b50);
    }

    function test_GapResetsStreak() public {
        _cleanEpochs(0, 29);
        (, bool b) = _finalize(31, 10_000); // epoch 29/30 never sealed for this node
        assertFalse(b);
        assertEq(rel.highStreakOf(node), 1);
    }

    function test_BelowThresholdNoBonus() public {
        vm.prank(configOwner);
        cfg.setUint(ConfigKeys.RELIABILITY_RECOVERY_BPS, 0);
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 1);
        _fail(ReliabilityRegistry.ChallengeKind.Por, 0, 2);
        _fail(ReliabilityRegistry.ChallengeKind.ResourceProbe, 0, 3); // 0.95
        _cleanEpochs(0, 40);
        (uint256 s, bool b) = rel.reliabilityOf(node, 39);
        assertEq(s, 9_500);
        assertFalse(b);
    }

    function testFuzz_Deterministic(uint8[16] memory events) public {
        // Same history replayed on a fresh registry yields identical scores.
        uint256[] memory first = _replay(events);
        setUp();
        uint256[] memory second = _replay(events);
        assertEq(keccak256(abi.encode(first)), keccak256(abi.encode(second)));
    }

    function _replay(uint8[16] memory events) internal returns (uint256[] memory scores) {
        scores = new uint256[](16);
        for (uint256 i; i < 16; ++i) {
            uint8 ev = events[i] % 5;
            if (ev < 3) _fail(ReliabilityRegistry.ChallengeKind(ev), i, i);
            (scores[i],) = _finalize(i, ev == 3 ? 9_000 : 10_000);
        }
    }
}

contract BonusNormalizationHarness {
    function normalize(uint256 pool, uint256[] memory base, bool[] memory eligible, uint256 bonusBps)
        external
        pure
        returns (uint256[] memory)
    {
        return BonusNormalization.normalize(pool, base, eligible, bonusBps);
    }
}

contract BonusNormalizationTest is Test {
    BonusNormalizationHarness internal h = new BonusNormalizationHarness();

    function _sum(uint256[] memory a) internal pure returns (uint256 s) {
        for (uint256 i; i < a.length; ++i) {
            s += a[i];
        }
    }

    function test_GherkinTenNodesThreeEligible() public view {
        uint256 pool = 100_000e18;
        uint256[] memory base = new uint256[](10);
        bool[] memory el = new bool[](10);
        for (uint256 i; i < 10; ++i) {
            base[i] = 10_000e18;
            el[i] = i < 3;
        }
        uint256[] memory out = h.normalize(pool, base, el, 10_500);
        for (uint256 i; i < 3; ++i) {
            assertEq(out[i], 10_500e18);
        }
        // 7 peers absorb the 1,500 surplus: (100k - 31.5k)/7 each (+dust on one).
        for (uint256 i = 3; i < 10; ++i) {
            assertApproxEqAbs(out[i], uint256(68_500e18) / 7, 6); // dust <= n-1 wei lands on one peer
        }
        assertEq(_sum(out), pool);
    }

    function test_AllEligibleFallsBackToBase() public view {
        uint256[] memory base = new uint256[](3);
        bool[] memory el = new bool[](3);
        (base[0], base[1], base[2]) = (100, 200, 700);
        (el[0], el[1], el[2]) = (true, true, true);
        uint256[] memory out = h.normalize(1_000, base, el, 10_500);
        assertEq(out[0], 100);
        assertEq(out[1], 200);
        assertEq(out[2], 700);
    }

    function test_NoneEligibleKeepsProportions() public view {
        uint256[] memory base = new uint256[](2);
        bool[] memory el = new bool[](2);
        (base[0], base[1]) = (333, 666);
        uint256[] memory out = h.normalize(1_000, base, el, 10_500);
        assertEq(_sum(out), 1_000); // rounding gap folded back
    }

    function test_SingleNodePool() public view {
        uint256[] memory base = new uint256[](1);
        bool[] memory el = new bool[](1);
        base[0] = 500;
        el[0] = true;
        uint256[] memory out = h.normalize(500, base, el, 10_500);
        assertEq(out[0], 500);
    }

    function test_SurplusExceedsPeersFallsBack() public view {
        // Eligible hold 99%: 5% surplus (~495) > peers' 10 -> proportional fallback.
        uint256[] memory base = new uint256[](2);
        bool[] memory el = new bool[](2);
        (base[0], base[1]) = (9_900, 100);
        (el[0], el[1]) = (true, false);
        uint256[] memory out = h.normalize(10_000, base, el, 10_500);
        assertEq(_sum(out), 10_000);
        assertGt(out[0], 9_900);
        assertGt(out[1], 0);
    }

    function test_RevertWhen_SharesExceedPool() public {
        uint256[] memory base = new uint256[](1);
        base[0] = 2;
        vm.expectRevert(abi.encodeWithSelector(BonusNormalization.SharesExceedPool.selector, 2, 1));
        h.normalize(1, base, new bool[](1), 10_500);
    }

    function testFuzz_SumAlwaysEqualsPool(uint96[8] memory raw, uint8 mask, uint96 extra) public view {
        uint256[] memory base = new uint256[](8);
        bool[] memory el = new bool[](8);
        uint256 total;
        for (uint256 i; i < 8; ++i) {
            base[i] = raw[i];
            el[i] = (mask >> i) & 1 == 1;
            total += raw[i];
        }
        vm.assume(total > 0);
        uint256 pool = total + extra;
        uint256[] memory out = h.normalize(pool, base, el, 10_500);
        assertEq(_sum(out), pool);
        // Eligible never get less than base, non-eligible never more than base (closed form)
        // except where the rounding gap `extra` is redistributed.
        if (extra == 0) {
            for (uint256 i; i < 8; ++i) {
                if (el[i]) assertGe(out[i], base[i]);
            }
        }
    }
}
