// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {EpochController} from "../src/EpochController.sol";
import {MockProtocolConfig} from "../src/config/MockProtocolConfig.sol";
import {ConfigKeys} from "../src/config/ConfigKeys.sol";

/// @notice Downstream-consumer stand-in (#72 QA G-series): gates on the
/// controller instead of doing its own epoch math.
contract EpochConsumer {
    EpochController internal immutable epochs;

    constructor(EpochController epochs_) {
        epochs = epochs_;
    }

    function sealAndRead(uint256 epoch) external returns (bool) {
        epochs.trySeal(epoch);
        return epochs.isSealed(epoch);
    }
}

/// @notice Issue #72 QA plan, sections A-H. Boundary tests use the real 24h
/// epoch and 15-minute window; governance tests also use a 1h epoch so the
/// length isn't hardcoded anywhere.
contract EpochControllerTest is Test {
    uint32 internal constant DAY = 1 days;
    uint32 internal constant WINDOW = 15 minutes;

    MockProtocolConfig internal cfg;
    EpochController internal epochs;
    address internal configOwner = makeAddr("configOwner");
    address internal timelock = makeAddr("timelock");
    uint64 internal genesis;

    function setUp() public {
        vm.warp(1_750_000_000);
        genesis = uint64(block.timestamp);
        cfg = new MockProtocolConfig(configOwner);
        vm.prank(configOwner);
        cfg.setAddress(ConfigKeys.GOVERNANCE_TIMELOCK, timelock);
        epochs = new EpochController(cfg, genesis, DAY, WINDOW);
    }

    // --- A. Epoch index ---

    function test_EpochIndexBoundaries() public view {
        assertEq(epochs.epochAt(genesis), 0); // A1
        assertEq(epochs.epochAt(genesis + DAY - 1), 0); // A3
        assertEq(epochs.epochAt(genesis + DAY), 1); // A4
        assertEq(epochs.epochAt(genesis + DAY + 1), 1); // A5
        assertEq(epochs.epochAt(genesis + 25 hours), 1); // A6 / Gherkin 1
        assertEq(epochs.currentEpoch(), 0); // A10
    }

    function test_CurrentEpochAt25Hours() public {
        vm.warp(genesis + 25 hours);
        assertEq(epochs.currentEpoch(), 1);
    }

    function test_RevertWhen_BeforeGenesis() public {
        // A2 / H4: pre-genesis timestamps have no epoch.
        vm.expectRevert(abi.encodeWithSelector(EpochController.BeforeGenesis.selector, genesis - 1, genesis));
        epochs.epochAt(genesis - 1);
    }

    function test_FutureGenesisDeploysButHasNoCurrentEpoch() public {
        EpochController future = new EpochController(cfg, genesis + DAY, DAY, WINDOW);
        vm.expectRevert(abi.encodeWithSelector(EpochController.BeforeGenesis.selector, block.timestamp, genesis + DAY));
        future.currentEpoch();
        assertFalse(future.acceptsReports(0));
    }

    function testFuzz_MatchesOffChainFormula(uint256 offset) public view {
        // A7 / A9: on-chain index always equals floor((t - genesis) / length).
        offset = bound(offset, 0, type(uint64).max);
        assertEq(epochs.epochAt(genesis + offset), offset / DAY);
    }

    function test_AdvancesOncePerDay() public {
        // A8: sampled hourly over 10 days, the index never skips or repeats.
        uint256 last;
        for (uint256 h = 1; h <= 240; ++h) {
            vm.warp(genesis + h * 1 hours);
            uint256 e = epochs.currentEpoch();
            assertTrue(e == last || e == last + 1);
            if (e == last + 1) assertEq(block.timestamp, epochs.epochStart(e));
            last = e;
        }
        assertEq(last, 10);
    }

    // --- B. Finality window / report acceptance (late reports are rejected, never rolled forward) ---

    function test_ReportAcceptanceBoundaries() public {
        uint256 end = epochs.epochEnd(5);
        vm.warp(end); // B1
        assertTrue(epochs.acceptsReports(5));
        vm.warp(end + 1); // B2
        assertTrue(epochs.acceptsReports(5));
        vm.warp(end + 10 minutes); // B3 / Gherkin 2
        assertTrue(epochs.acceptsReports(5));
        vm.warp(end + WINDOW); // B4: inclusive
        assertTrue(epochs.acceptsReports(5));
        vm.warp(end + WINDOW + 1); // B5
        assertFalse(epochs.acceptsReports(5));
        vm.warp(end + 16 minutes); // B6 / Gherkin 3
        assertFalse(epochs.acceptsReports(5));
        assertTrue(epochs.acceptsReports(6)); // the next epoch is open, but nothing rolls into it
    }

    function test_FutureEpochDoesNotAcceptReports() public view {
        // B7
        assertFalse(epochs.acceptsReports(1));
        assertTrue(epochs.acceptsReports(0));
    }

    function test_FinalityWindowQuery() public {
        // B9
        uint256 end = epochs.epochEnd(5);
        vm.warp(end - 1);
        assertFalse(epochs.isWithinFinalityWindow(5));
        vm.warp(end);
        assertTrue(epochs.isWithinFinalityWindow(5));
        vm.warp(end + WINDOW);
        assertTrue(epochs.isWithinFinalityWindow(5));
        vm.warp(end + WINDOW + 1);
        assertFalse(epochs.isWithinFinalityWindow(5));
    }

    // --- C. Sealing gate ---

    function test_SealBoundaries() public {
        uint256 end = epochs.epochEnd(5);
        uint256[3] memory early = [end + 5 minutes, end + WINDOW - 1, end + WINDOW]; // C1, C2, C3
        for (uint256 i; i < 3; ++i) {
            vm.warp(early[i]);
            assertFalse(epochs.isSealed(5));
            vm.expectRevert(abi.encodeWithSelector(EpochController.FinalityWindowOpen.selector, 5, end + WINDOW));
            epochs.trySeal(5);
        }
        vm.warp(end + WINDOW + 1); // C4
        assertTrue(epochs.isSealed(5)); // time-based, before anyone calls trySeal
        vm.expectEmit(true, false, false, true, address(epochs));
        emit EpochController.EpochSealed(5, epochs.epochStart(5), end);
        assertTrue(epochs.trySeal(5));
    }

    function test_AnyoneAndContractsCanSeal() public {
        vm.warp(epochs.finalityDeadline(5) + 1 minutes);
        vm.prank(makeAddr("stranger")); // C5 / Gherkin 5
        epochs.trySeal(5);
        assertTrue(epochs.isSealed(5));
        EpochConsumer consumer = new EpochConsumer(epochs); // C6 / G6
        assertTrue(consumer.sealAndRead(4));
    }

    function test_EpochsSealInAnyOrder() public {
        // C7: sealing N doesn't require N-1 to be recorded first.
        vm.warp(epochs.finalityDeadline(5) + 1);
        epochs.trySeal(5);
        epochs.trySeal(2);
        assertTrue(epochs.isSealed(3));
    }

    function test_RevertWhen_SealingCurrentOrFutureEpoch() public {
        // C8 / H1
        vm.expectRevert(abi.encodeWithSelector(EpochController.FinalityWindowOpen.selector, 0, epochs.finalityDeadline(0)));
        epochs.trySeal(0);
        vm.expectRevert(abi.encodeWithSelector(EpochController.FinalityWindowOpen.selector, 9, epochs.finalityDeadline(9)));
        epochs.trySeal(9);
    }

    // --- D. Idempotency: repeat seals are silent no-ops ---

    function test_RepeatSealIsNoOp() public {
        vm.warp(epochs.finalityDeadline(5) + 1);
        epochs.trySeal(5);
        vm.recordLogs();
        vm.prank(makeAddr("second")); // D1, D3
        assertTrue(epochs.trySeal(5));
        assertTrue(epochs.trySeal(5)); // D2: same block
        assertEq(vm.getRecordedLogs().length, 0); // no duplicate EpochSealed
        assertFalse(epochs.isSealed(6)); // D4: never touched a neighbouring epoch
    }

    // --- E. Immutability ---

    function test_SealedEpochStaysSealedAndFixed() public {
        vm.warp(epochs.finalityDeadline(5) + 1);
        uint256 start = epochs.epochStart(5);
        uint256 end = epochs.epochEnd(5);
        epochs.trySeal(5);
        vm.warp(block.timestamp + 30 days); // E5
        vm.prank(timelock);
        epochs.setEpochLength(12 hours); // E3: governance can't touch it either
        vm.warp(block.timestamp + 30 days);
        assertTrue(epochs.isSealed(5)); // E4
        assertEq(epochs.epochStart(5), start);
        assertEq(epochs.epochEnd(5), end);
    }

    // --- F. Governance length changes are prospective only ---

    function test_LengthChangeIsProspective() public {
        // F1 / Gherkin 7: epochs 0-10 under 24h, then governance moves to 12h.
        vm.warp(epochs.finalityDeadline(10) + 1);
        uint256[11] memory starts;
        for (uint256 e; e <= 10; ++e) {
            epochs.trySeal(e);
            starts[e] = epochs.epochStart(e);
        }
        uint256 current = epochs.currentEpoch(); // 11
        uint256 boundary = epochs.epochEnd(current);

        vm.expectEmit(true, false, false, true, address(epochs));
        emit EpochController.EpochLengthChanged(current + 1, boundary, 12 hours);
        vm.prank(timelock);
        epochs.setEpochLength(12 hours);

        for (uint256 e; e <= 10; ++e) {
            assertEq(epochs.epochStart(e), starts[e]);
        }
        // F3: the in-progress epoch finishes at its original 24h length.
        assertEq(epochs.epochEnd(current) - epochs.epochStart(current), DAY);
        assertEq(epochs.epochAt(boundary - 1), current);
        // F2: the new length applies from the next boundary on.
        assertEq(epochs.epochAt(boundary), current + 1);
        assertEq(epochs.epochAt(boundary + 12 hours), current + 2);
        assertEq(epochs.epochEnd(current + 1) - epochs.epochStart(current + 1), 12 hours);
        assertEq(epochs.epochLength(), 12 hours);
    }

    function test_FinalityOfPreChangeEpochUnaffected() public {
        // F4: an epoch still in its finality window keeps its original deadline.
        vm.warp(epochs.epochEnd(3) + 5 minutes);
        uint256 deadline = epochs.finalityDeadline(3);
        vm.prank(timelock);
        epochs.setEpochLength(12 hours);
        assertEq(epochs.finalityDeadline(3), deadline);
        assertTrue(epochs.acceptsReports(3));
    }

    function test_ConsecutiveLengthChanges() public {
        // F5 / F6: 24h -> 12h -> 6h, each segment reproducible from the public checkpoints.
        vm.prank(timelock);
        epochs.setEpochLength(12 hours); // effective epoch 1
        vm.warp(epochs.epochStart(3));
        vm.prank(timelock);
        epochs.setEpochLength(6 hours); // effective epoch 4
        assertEq(epochs.checkpointCount(), 3);

        for (uint256 e; e < 8; ++e) {
            uint256 expected = e == 0 ? genesis : e <= 4 ? genesis + DAY + (e - 1) * 12 hours : genesis + DAY + 3 * 12 hours + (e - 4) * 6 hours;
            assertEq(epochs.epochStart(e), expected);
            assertEq(epochs.epochAt(expected), e);
            assertEq(_offChainEpochAt(expected), e);
        }
    }

    function test_PendingChangeReplacedBeforeItTakesEffect() public {
        vm.startPrank(timelock);
        epochs.setEpochLength(12 hours);
        epochs.setEpochLength(6 hours);
        vm.stopPrank();
        assertEq(epochs.checkpointCount(), 2);
        assertEq(epochs.epochEnd(1) - epochs.epochStart(1), 6 hours);
    }

    function test_RevertWhen_NonGovernanceChangesLength() public {
        // F7
        vm.expectRevert(abi.encodeWithSelector(EpochController.NotGovernance.selector, address(this)));
        epochs.setEpochLength(12 hours);
        vm.prank(configOwner);
        vm.expectRevert(abi.encodeWithSelector(EpochController.NotGovernance.selector, configOwner));
        epochs.setEpochLength(12 hours);
    }

    function test_RevertWhen_InvalidLengths() public {
        vm.startPrank(timelock);
        vm.expectRevert(abi.encodeWithSelector(EpochController.InvalidEpochLength.selector, 59 minutes));
        epochs.setEpochLength(59 minutes);
        vm.stopPrank();
        vm.expectRevert(abi.encodeWithSelector(EpochController.InvalidFinalityWindow.selector, 0));
        new EpochController(cfg, genesis, DAY, 0);
        vm.expectRevert(abi.encodeWithSelector(EpochController.InvalidFinalityWindow.selector, DAY));
        new EpochController(cfg, genesis, DAY, DAY);
        vm.expectRevert(EpochController.ZeroAddress.selector);
        new EpochController(MockProtocolConfig(address(0)), genesis, DAY, WINDOW);
    }

    // --- G. Query interface ---

    function test_BoundsConsistent() public {
        // G2
        vm.warp(genesis + 3 * DAY + 7 hours);
        uint256 e = epochs.currentEpoch();
        assertEq(epochs.epochStart(e), genesis + e * DAY);
        assertEq(epochs.epochEnd(e), genesis + (e + 1) * DAY);
        assertEq(epochs.epochEndTimestamp(e), epochs.epochEnd(e));
        assertEq(epochs.finalityDeadline(e), epochs.epochEnd(e) + WINDOW);
    }

    function test_IsSealedStates() public {
        // G4: sealed (recorded), sealed (time only), still open.
        vm.warp(epochs.finalityDeadline(2) + 1);
        epochs.trySeal(1);
        assertTrue(epochs.isSealed(1));
        assertTrue(epochs.isSealed(2));
        assertFalse(epochs.isSealed(3));
    }

    // --- H. Drift ---

    function testFuzz_ReportNeverAcceptedIntoTwoClosedEpochs(uint256 t) public {
        // H3: at any second, at most the current epoch and the one in finality accept reports.
        t = bound(t, genesis, genesis + 400 days);
        vm.warp(t);
        uint256 e = epochs.currentEpoch();
        uint256 accepting;
        for (uint256 k = e >= 2 ? e - 2 : 0; k <= e + 1; ++k) {
            if (epochs.acceptsReports(k)) ++accepting;
        }
        assertTrue(accepting == 1 || accepting == 2);
        assertTrue(epochs.acceptsReports(e));
    }

    function _offChainEpochAt(uint256 t) internal view returns (uint256) {
        uint256 i = epochs.checkpointCount();
        EpochController.LengthCheckpoint memory cp;
        do {
            cp = epochs.checkpointAt(--i);
        } while (cp.effectiveTimestamp > t);
        return cp.effectiveEpoch + (t - cp.effectiveTimestamp) / cp.length;
    }
}
