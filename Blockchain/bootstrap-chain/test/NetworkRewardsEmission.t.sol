// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {ERC1967Proxy} from "@openzeppelin/contracts/proxy/ERC1967/ERC1967Proxy.sol";
import {SYMToken} from "../src/SYMToken.sol";
import {NetworkRewardsEmission} from "../src/NetworkRewardsEmission.sol";
import {IEpochClock} from "../src/interfaces/IEpochClock.sol";
import {IFeeTreasury} from "../src/interfaces/IFeeTreasury.sol";
import {ISYMMinter} from "../src/interfaces/ISYMMinter.sol";

contract MockEpochClock is IEpochClock {
    uint256 public immutable genesis;

    constructor(uint256 genesis_) {
        genesis = genesis_;
    }

    function epochEndTimestamp(uint256 epochIndex) external view returns (uint256) {
        return genesis + (epochIndex + 1) * 1 days;
    }
}

/// @dev Stand-in for #86: configurable fee share and a treasury balance
/// that pays out partially on shortfall.
contract MockFeeTreasury is IFeeTreasury {
    SYMToken internal immutable token;
    mapping(uint256 => uint256) public feeShare;
    uint256 public balance;
    bool public reenter;
    NetworkRewardsEmission public emission;

    constructor(SYMToken token_) {
        token = token_;
    }

    function setFeeShare(uint256 epochIndex, uint256 amount) external {
        feeShare[epochIndex] = amount;
    }

    function setBalance(uint256 amount) external {
        balance = amount;
    }

    function armReentrancy(NetworkRewardsEmission e) external {
        emission = e;
        reenter = true;
    }

    function getProtocolFeeShareToRunners(uint256 epochIndex) external view returns (uint256) {
        return feeShare[epochIndex];
    }

    function drawFeeTreasury(uint256 epochIndex, uint256 requested, address) external returns (uint256 paid) {
        if (reenter) {
            reenter = false;
            emission.emitForEpoch(epochIndex);
        }
        paid = requested < balance ? requested : balance;
        balance -= paid;
    }
}

contract NetworkRewardsEmissionTest is Test {
    uint256 internal constant BUCKET = 4_000_000_000e18;

    SYMToken internal token;
    NetworkRewardsEmission internal emission;
    MockEpochClock internal clock;
    MockFeeTreasury internal treasury;
    address internal distributor = makeAddr("rewardDistributor");
    address internal authority = makeAddr("tier3");

    function setUp() public {
        clock = new MockEpochClock(block.timestamp);

        // Deploy order: emission needs the token address and the token's
        // initializer needs the emission address, so predict the proxy address.
        address impl = address(new SYMToken());
        address predictedTreasury = vm.computeCreateAddress(address(this), vm.getNonce(address(this)) + 1);
        address predictedToken = vm.computeCreateAddress(address(this), vm.getNonce(address(this)) + 2);
        emission = new NetworkRewardsEmission(
            ISYMMinter(predictedToken), distributor, clock, IFeeTreasury(predictedTreasury)
        );
        treasury = new MockFeeTreasury(SYMToken(predictedToken));

        address[] memory m = new address[](2);
        uint256[] memory a = new uint256[](2);
        (m[0], a[0]) = (address(emission), BUCKET);
        (m[1], a[1]) = (makeAddr("otherBuckets"), 6_000_000_000e18);
        token = SYMToken(
            address(new ERC1967Proxy(impl, abi.encodeCall(SYMToken.initialize, ("Symposia", "SYM", authority, m, a))))
        );
        assertEq(address(token), predictedToken);
        assertEq(address(treasury), predictedTreasury);
    }

    function _warpPast(uint256 epochIndex) internal {
        vm.warp(clock.epochEndTimestamp(epochIndex));
    }

    function _setCumulativeMinted(uint256 amount) internal {
        vm.store(address(emission), bytes32(uint256(0)), bytes32(amount));
        assertEq(emission.cumulativeMinted(), amount);
    }

    // --- A. Year table (FR1) ---

    function test_YearTableMatchesPublishedTotals() public view {
        uint256[8] memory totals = [
            uint256(800_000_000e18),
            640_000_000e18,
            560_000_000e18,
            480_000_000e18,
            440_000_000e18,
            400_000_000e18,
            360_000_000e18,
            320_000_000e18
        ];
        uint256 sum;
        for (uint256 y; y < 8; ++y) {
            assertEq(emission.yearlyTotal(y), totals[y]);
            assertEq(emission.emissionPerEpoch(y), totals[y] / emission.epochsInYear(y));
            sum += totals[y];
        }
        assertEq(sum, BUCKET);
        assertEq(emission.epochsInYear(7), 367);
        assertEq(emission.emissionPerEpoch(8), 0);
    }

    function test_Year1PerEpochMatchesDoc() public view {
        // tokenomics-mvp.md §5.3: ~2,191,781 SYM / epoch in year 1.
        assertEq(emission.primaryEmission(100) / 1e18, 2_191_780);
        assertEq(emission.primaryEmission(100), uint256(800_000_000e18) / 365);
    }

    // --- B. Exact 4B exhaustion (FR4) ---

    function test_PrimaryScheduleSumsToExactly4B() public view {
        uint256 sum;
        for (uint256 e; e < 2922; ++e) {
            sum += emission.primaryEmission(e);
        }
        assertEq(sum, BUCKET);
        assertEq(emission.primaryEmission(2922), 0);
    }

    function test_EachYearSumsExactly() public view {
        for (uint256 y; y < 8; ++y) {
            uint256 start = y * 365;
            uint256 sum;
            for (uint256 e = start; e < start + emission.epochsInYear(y); ++e) {
                sum += emission.primaryEmission(e);
            }
            assertEq(sum, emission.yearlyTotal(y));
        }
    }

    function test_FullHorizonOnChainMintsExactly4B() public {
        _warpPast(2921);
        for (uint256 e; e < 2922; ++e) {
            emission.emitForEpoch(e);
        }
        assertEq(token.balanceOf(distributor), BUCKET);
        assertEq(emission.cumulativeMinted(), BUCKET);
        assertTrue(emission.isBucketExhausted());
        assertEq(token.remainingMintAllowance(address(emission)), 0);
    }

    // --- C. Year index & boundaries (FR2) ---

    function test_YearIndexBoundaries() public view {
        assertEq(emission.yearIndexOf(0), 0);
        assertEq(emission.yearIndexOf(364), 0);
        assertEq(emission.yearIndexOf(365), 1);
        assertEq(emission.yearIndexOf(729), 1);
        assertEq(emission.yearIndexOf(730), 2);
        assertEq(emission.yearIndexOf(2554), 6);
        assertEq(emission.yearIndexOf(2555), 7);
        assertEq(emission.yearIndexOf(2921), 7);
        assertEq(emission.yearIndexOf(2922), 7);
        assertEq(emission.yearIndexOf(type(uint256).max), 7);
    }

    function test_YearEndEpochAbsorbsRemainder() public view {
        uint256 per0 = emission.emissionPerEpoch(0);
        assertEq(emission.primaryEmission(363), per0);
        assertEq(emission.primaryEmission(364), per0 + (800_000_000e18 - per0 * 365));
        assertEq(emission.primaryEmission(365), emission.emissionPerEpoch(1));
        // Year 8 remainder lands on 2921, not on 2554+365 = 2919.
        uint256 per7 = emission.emissionPerEpoch(7);
        assertEq(emission.primaryEmission(2919), per7);
        assertEq(emission.primaryEmission(2921), per7 + (320_000_000e18 - per7 * 367));
    }

    function testFuzz_YearIndexFormula(uint256 epochIndex) public view {
        uint256 expected = epochIndex / 365;
        if (expected > 7) expected = 7;
        assertEq(emission.yearIndexOf(epochIndex), expected);
    }

    // --- D. Per-epoch mint & double-mint (FR3, AC4) ---

    function test_EmitMintsToDistributorAndEmits() public {
        _warpPast(100);
        uint256 expected = emission.primaryEmission(100);
        assertEq(emission.previewEpochEmission(100), expected);
        vm.expectEmit(true, false, false, true);
        emit NetworkRewardsEmission.EpochEmitted(100, expected, 0, true, 0);
        assertEq(emission.emitForEpoch(100), expected);
        assertEq(token.balanceOf(distributor), expected);
        assertTrue(emission.isEmitted(100));
        assertEq(emission.emittedAmount(100), expected);
        assertEq(emission.previewEpochEmission(100), 0);
    }

    function test_RepeatEmitIsNoOpReturningRecordedAmount() public {
        _warpPast(500);
        uint256 first = emission.emitForEpoch(500);
        uint256 supply = token.totalSupply();
        assertEq(emission.emitForEpoch(500), first);
        assertEq(token.totalSupply(), supply);
    }

    function test_RevertWhen_EpochNotEnded() public {
        uint256 endsAt = clock.epochEndTimestamp(5);
        vm.warp(endsAt - 1);
        vm.expectRevert(abi.encodeWithSelector(NetworkRewardsEmission.EpochNotEnded.selector, 5, endsAt));
        emission.emitForEpoch(5);
    }

    function test_OutOfOrderBackfill() public {
        _warpPast(10);
        emission.emitForEpoch(10);
        emission.emitForEpoch(3);
        assertEq(token.balanceOf(distributor), emission.primaryEmission(10) + emission.primaryEmission(3));
    }

    function test_DirectMintByOutsiderReverts() public {
        vm.expectRevert(abi.encodeWithSelector(SYMToken.NotMinter.selector, address(this)));
        token.mint(address(this), 1);
    }

    // --- E/F. Post-primary floor (FR5, FR6) ---

    function test_FloorTopsUpFromTreasuryWhenFeeShareLow() public {
        treasury.setFeeShare(2922, 30_000e18);
        treasury.setBalance(1_000_000e18);
        _warpPast(2922);
        uint256 delivered = emission.emitForEpoch(2922);
        assertEq(delivered, 20_000e18); // gap funded by treasury
        assertEq(treasury.balance(), 1_000_000e18 - 20_000e18);
        assertEq(token.totalSupply(), 0); // nothing minted
    }

    function test_FloorNoTopUpWhenFeeShareAboveFloor() public {
        treasury.setFeeShare(3000, 75_000e18);
        treasury.setBalance(1_000_000e18);
        _warpPast(3000);
        assertEq(emission.emitForEpoch(3000), 0);
        assertEq(treasury.balance(), 1_000_000e18);
    }

    function test_FloorNoTopUpWhenFeeShareExactlyAtFloor() public {
        treasury.setFeeShare(3000, 50_000e18);
        _warpPast(3000);
        vm.recordLogs();
        assertEq(emission.emitForEpoch(3000), 0);
        assertEq(vm.getRecordedLogs().length, 1); // EpochEmitted only, no shortfall alert
    }

    function test_FloorShortfallAlertWhenTreasuryEmptyAndBucketExhausted() public {
        _setCumulativeMinted(BUCKET);
        treasury.setFeeShare(2950, 5_000e18);
        _warpPast(2950);
        vm.expectEmit(true, false, false, true);
        emit NetworkRewardsEmission.FloorShortfallAlert(2950, 50_000e18, 5_000e18);
        assertEq(emission.emitForEpoch(2950), 0); // runners get only the 5k fee share
    }

    function test_FloorPartialTreasuryThenBucket() public {
        // Bucket has unminted allowance only if primary epochs were skipped.
        _setCumulativeMinted(BUCKET - 10_000e18);
        treasury.setFeeShare(2950, 5_000e18);
        treasury.setBalance(15_000e18);
        _warpPast(2950);
        vm.expectEmit(true, false, false, true);
        emit NetworkRewardsEmission.FloorShortfallAlert(2950, 50_000e18, 30_000e18);
        uint256 delivered = emission.emitForEpoch(2950);
        assertEq(delivered, 25_000e18); // 15k treasury + 10k bucket
        assertEq(token.balanceOf(distributor), 10_000e18);
        assertTrue(emission.isBucketExhausted());
    }

    function test_FloorNeverMintsPastRemainingBucket() public {
        _setCumulativeMinted(BUCKET - 10_000e18);
        _warpPast(2960);
        vm.expectEmit(false, false, false, true);
        emit NetworkRewardsEmission.BucketExhausted(2959);
        emission.emitForEpoch(2959);
        assertEq(token.balanceOf(distributor), 10_000e18);
        assertEq(emission.emitForEpoch(2960), 0);
        assertEq(token.balanceOf(distributor), 10_000e18);
    }

    function test_FloorWorksWithoutFeeTreasury() public {
        NetworkRewardsEmission bare = new NetworkRewardsEmission(ISYMMinter(address(token)), distributor, clock, IFeeTreasury(address(0)));
        _warpPast(3000);
        // Not a token minter, but with no remaining bucket configured nothing is minted:
        vm.store(address(bare), bytes32(uint256(0)), bytes32(BUCKET));
        assertEq(bare.emitForEpoch(3000), 0);
    }

    function test_ReentrantTreasuryCannotDoubleEmit() public {
        _setCumulativeMinted(BUCKET - 100_000e18);
        treasury.armReentrancy(emission);
        _warpPast(2950);
        emission.emitForEpoch(2950);
        assertEq(token.balanceOf(distributor), 50_000e18);
    }

    function test_RevertWhen_ZeroConstructorArgs() public {
        vm.expectRevert(NetworkRewardsEmission.ZeroAddress.selector);
        new NetworkRewardsEmission(ISYMMinter(address(0)), distributor, clock, treasury);
    }

    // --- G. Ledger reconciliation (FR8) ---

    function testFuzz_LedgersAgree(uint256[8] memory epochs) public {
        _warpPast(3500);
        for (uint256 i; i < epochs.length; ++i) {
            emission.emitForEpoch(bound(epochs[i], 0, 3500));
        }
        assertEq(emission.cumulativeMinted(), BUCKET - token.remainingMintAllowance(address(emission)));
        assertEq(token.balanceOf(distributor), emission.cumulativeMinted());
        assertLe(emission.cumulativeMinted(), BUCKET);
    }
}

contract EmissionHandler is Test {
    NetworkRewardsEmission internal emission;

    constructor(NetworkRewardsEmission e) {
        emission = e;
    }

    function emitEpoch(uint256 epochIndex) external {
        emission.emitForEpoch(bound(epochIndex, 0, 4000));
    }
}

contract NetworkRewardsEmissionInvariantTest is Test {
    SYMToken internal token;
    NetworkRewardsEmission internal emission;
    address internal distributor = makeAddr("rewardDistributor");

    function setUp() public {
        MockEpochClock clock = new MockEpochClock(block.timestamp);
        address impl = address(new SYMToken());
        address predictedToken = vm.computeCreateAddress(address(this), vm.getNonce(address(this)) + 1);
        emission = new NetworkRewardsEmission(ISYMMinter(predictedToken), distributor, clock, IFeeTreasury(address(0)));
        address[] memory m = new address[](1);
        uint256[] memory a = new uint256[](1);
        (m[0], a[0]) = (address(emission), 4_000_000_000e18);
        token = SYMToken(
            address(new ERC1967Proxy(impl, abi.encodeCall(SYMToken.initialize, ("Symposia", "SYM", address(1), m, a))))
        );
        vm.warp(clock.epochEndTimestamp(4000));
        targetContract(address(new EmissionHandler(emission)));
    }

    function invariant_NeverExceedsBucketAndLedgersAgree() public view {
        assertLe(emission.cumulativeMinted(), 4_000_000_000e18);
        assertEq(token.totalMinted(), emission.cumulativeMinted());
        assertEq(token.balanceOf(distributor), emission.cumulativeMinted());
    }
}
