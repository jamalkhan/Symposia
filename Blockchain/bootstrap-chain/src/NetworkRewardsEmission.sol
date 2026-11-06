// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IEpochClock} from "./interfaces/IEpochClock.sol";
import {IFeeTreasury} from "./interfaces/IFeeTreasury.sol";
import {ISYMMinter} from "./interfaces/ISYMMinter.sol";
import {SafeCast} from "@openzeppelin/contracts/utils/math/SafeCast.sol";

/// @title NetworkRewardsEmission
/// @notice Mint-amount oracle and mint trigger for the 4B SYM Network
/// Rewards bucket (issue #73, tokenomics-mvp.md §5.1-5.4). Holds no SYM and
/// no distribution logic: each epoch's emission is minted straight to the
/// RewardDistributor, which splits it (#80/#81).
///
/// Primary horizon (epochs 0..2921): yearly step-down table, uniform within
/// a year. `yearIndex = min(7, epoch / 365)`; year 8 (index 7) covers the
/// 367 epochs 2555..2921. Per-epoch amount is `yearlyTotal / epochsInYear`
/// (integer division), and the **last epoch of each year absorbs that
/// year's remainder**. Together these sum to exactly 4,000,000,000 SYM. All
/// values are compile-time constants, so anyone can recompute any epoch's
/// emission from this source alone.
///
/// Post-primary (epoch >= 2922), per §5.4: runners get
/// `max(feeShare, MIN_EPOCH_EMISSION)`. The fee share itself is delivered by
/// the fee splitter (#86) and is never minted here. This contract funds only
/// the top-up gap `MIN_EPOCH_EMISSION - feeShare`: first from the fee
/// treasury, then by minting from whatever Network Rewards allowance is
/// still unminted. Any gap left after both emits `FloorShortfallAlert`, and
/// nothing is minted beyond the bucket. Because the primary schedule sums to
/// exactly 4B, the bucket is normally empty by floor mode. It only has
/// allowance left if some primary epochs were never emitted.
///
/// Decisions on the QA open questions:
/// - Primary epochs are never forfeited: any ended, not-yet-emitted epoch
///   can be emitted late, in any order (OQ3/OQ4). Late backfills and floor
///   top-ups share the remaining bucket, so both are capped by it.
/// - A repeated `emitForEpoch` for an emitted epoch is a no-op that returns
///   the recorded amount, so redundant seal -> emit -> distribute batches
///   never fail (Arch alternative 6).
/// - Gating: an epoch can be emitted once it has **ended**
///   (`IEpochClock.epochEndTimestamp`). It doesn't wait for seal, because
///   RewardDistributor needs the epoch's SYM in hand when it seals.
/// - The schedule is indexed by epoch and assumes 24h epochs (365/year).
///   Changing `EPOCH_LENGTH` is a Tier 3 tokenomics change that must ship
///   with a matching emission contract.
/// - Successors: a replacement emission contract (installed via a Tier 3
///   SYMToken upgrade) must inherit this contract's *remaining* allowance,
///   not a fresh 4B, and must refuse epochs this one already emitted
///   (import `isEmitted`/`cumulativeMinted` as its starting watermark).
/// - If the fee-share read or treasury draw reverts, the call reverts too
///   (OQ7). Silently treating it as zero would under-pay runners
///   permanently.
contract NetworkRewardsEmission {
    uint256 public constant EPOCHS_PER_YEAR = 365;
    uint256 public constant PRIMARY_HORIZON_EPOCHS = 2922;
    uint256 public constant LAST_YEAR_INDEX = 7;
    uint256 public constant BUCKET_TOTAL = 4_000_000_000e18;
    uint256 public constant MIN_EPOCH_EMISSION = 50_000e18;

    ISYMMinter public immutable token;
    address public immutable rewardDistributor;
    IEpochClock public immutable epochClock;
    /// @notice Tier 3 governance address, which may wire the #86 fee treasury exactly once.
    address public immutable governance;
    /// @notice Fee splitter / treasury (#86). Zero until governance wires it
    /// (once, via `setFeeTreasury`). While zero, floor mode treats fee share
    /// and treasury as zero. A settable-once slot avoids redeploying, and so
    /// losing the emitted-epoch ledger, just to connect #86.
    IFeeTreasury public feeTreasury;

    struct EpochEmission {
        uint128 amount; // total SYM delivered to the distributor by this contract for the epoch
        bool emitted;
    }

    uint256 public cumulativeMinted;
    mapping(uint256 => EpochEmission) private _emissions;

    event EpochEmitted(
        uint256 indexed epochIndex, uint256 minted, uint256 treasuryPaid, bool isPrimaryHorizon, uint256 yearIndex
    );
    event FloorShortfallAlert(uint256 indexed epochIndex, uint256 requested, uint256 paid);
    event BucketExhausted(uint256 finalEpochIndex);

    error EpochNotEnded(uint256 epochIndex, uint256 endsAt);
    error ZeroAddress();
    error NotGovernance(address caller);
    error FeeTreasuryAlreadySet();

    event FeeTreasurySet(address indexed feeTreasury);

    constructor(
        ISYMMinter token_,
        address rewardDistributor_,
        IEpochClock epochClock_,
        IFeeTreasury feeTreasury_,
        address governance_
    ) {
        if (
            address(token_) == address(0) || rewardDistributor_ == address(0) || address(epochClock_) == address(0)
                || governance_ == address(0)
        ) {
            revert ZeroAddress();
        }
        token = token_;
        rewardDistributor = rewardDistributor_;
        epochClock = epochClock_;
        feeTreasury = feeTreasury_;
        governance = governance_;
    }

    /// @notice Wires the #86 fee treasury. Governance only, and only while unset.
    function setFeeTreasury(IFeeTreasury feeTreasury_) external {
        if (msg.sender != governance) revert NotGovernance(msg.sender);
        if (address(feeTreasury) != address(0)) revert FeeTreasuryAlreadySet();
        if (address(feeTreasury_) == address(0)) revert ZeroAddress();
        feeTreasury = feeTreasury_;
        emit FeeTreasurySet(address(feeTreasury_));
    }

    // ---------------------------------------------------------------------
    // Schedule (pure, publicly reproducible)
    // ---------------------------------------------------------------------

    /// @notice Total SYM released in year `yearIndex` (0-based) of the primary horizon.
    function yearlyTotal(uint256 yearIndex) public pure returns (uint256) {
        if (yearIndex == 0) return 800_000_000e18;
        if (yearIndex == 1) return 640_000_000e18;
        if (yearIndex == 2) return 560_000_000e18;
        if (yearIndex == 3) return 480_000_000e18;
        if (yearIndex == 4) return 440_000_000e18;
        if (yearIndex == 5) return 400_000_000e18;
        if (yearIndex == 6) return 360_000_000e18;
        if (yearIndex == 7) return 320_000_000e18;
        return 0;
    }

    function epochsInYear(uint256 yearIndex) public pure returns (uint256) {
        if (yearIndex < LAST_YEAR_INDEX) return EPOCHS_PER_YEAR;
        if (yearIndex == LAST_YEAR_INDEX) return PRIMARY_HORIZON_EPOCHS - LAST_YEAR_INDEX * EPOCHS_PER_YEAR; // 367
        return 0;
    }

    /// @notice Uniform per-epoch emission for `yearIndex`, before the year-end remainder.
    function emissionPerEpoch(uint256 yearIndex) public pure returns (uint256) {
        uint256 n = epochsInYear(yearIndex);
        return n == 0 ? 0 : yearlyTotal(yearIndex) / n;
    }

    function yearIndexOf(uint256 epochIndex) public pure returns (uint256) {
        uint256 y = epochIndex / EPOCHS_PER_YEAR;
        return y > LAST_YEAR_INDEX ? LAST_YEAR_INDEX : y;
    }

    /// @notice Scheduled primary-horizon emission for `epochIndex`, including
    /// the year-end remainder on each year's last epoch. Zero for floor epochs.
    function primaryEmission(uint256 epochIndex) public pure returns (uint256) {
        if (epochIndex >= PRIMARY_HORIZON_EPOCHS) return 0;
        uint256 y = yearIndexOf(epochIndex);
        uint256 perEpoch = emissionPerEpoch(y);
        uint256 lastEpochOfYear = y * EPOCHS_PER_YEAR + epochsInYear(y) - 1;
        if (epochIndex == lastEpochOfYear) {
            return perEpoch + (yearlyTotal(y) - perEpoch * epochsInYear(y));
        }
        return perEpoch;
    }

    // ---------------------------------------------------------------------
    // Views
    // ---------------------------------------------------------------------

    function remainingBucketBalance() public view returns (uint256) {
        return BUCKET_TOTAL - cumulativeMinted;
    }

    function isBucketExhausted() external view returns (bool) {
        return cumulativeMinted == BUCKET_TOTAL;
    }

    function isEmitted(uint256 epochIndex) external view returns (bool) {
        return _emissions[epochIndex].emitted;
    }

    /// @notice SYM this contract delivered for an already-emitted epoch.
    function emittedAmount(uint256 epochIndex) external view returns (uint256) {
        return _emissions[epochIndex].amount;
    }

    /// @notice Bucket SYM that `emitForEpoch` would still mint for
    /// `epochIndex` given current state. Returns 0 once the epoch is emitted
    /// (see `emittedAmount` for what was delivered), not "nothing scheduled".
    /// Exact for primary epochs. For floor epochs it assumes the treasury
    /// pays nothing, which upper-bounds the mint.
    function previewEpochEmission(uint256 epochIndex) external view returns (uint256) {
        if (_emissions[epochIndex].emitted) return 0;
        uint256 remaining = remainingBucketBalance();
        if (epochIndex < PRIMARY_HORIZON_EPOCHS) return _min(primaryEmission(epochIndex), remaining);
        uint256 feeShare =
            address(feeTreasury) == address(0) ? 0 : feeTreasury.getProtocolFeeShareToRunners(epochIndex);
        uint256 gap = feeShare >= MIN_EPOCH_EMISSION ? 0 : MIN_EPOCH_EMISSION - feeShare;
        return _min(gap, remaining);
    }

    // ---------------------------------------------------------------------
    // Emission
    // ---------------------------------------------------------------------

    /// @notice Delivers `epochIndex`'s emission to the RewardDistributor.
    /// Permissionless; reverts only if the epoch hasn't ended. Idempotent:
    /// returns the recorded amount for an epoch that was already emitted.
    /// @return delivered Total SYM the distributor has to distribute for this
    /// epoch: bucket mint, plus any treasury draw, plus (in floor mode) the
    /// #86 fee share, which #86 must deliver to the distributor before seal.
    function emitForEpoch(uint256 epochIndex) external returns (uint256 delivered) {
        EpochEmission storage rec = _emissions[epochIndex];
        if (rec.emitted) return rec.amount;

        uint256 endsAt = epochClock.epochEndTimestamp(epochIndex);
        if (block.timestamp < endsAt) revert EpochNotEnded(epochIndex, endsAt);

        // Effects before interactions: latch the epoch before any external
        // call so a reentrant treasury cannot emit it twice.
        rec.emitted = true;

        bool primary = epochIndex < PRIMARY_HORIZON_EPOCHS;
        uint256 toMint;
        uint256 treasuryPaid;
        uint256 feeShare;
        if (primary) {
            toMint = _min(primaryEmission(epochIndex), remainingBucketBalance());
        } else {
            (toMint, treasuryPaid, feeShare) = _floor(epochIndex);
        }

        if (toMint > 0) {
            cumulativeMinted += toMint;
            token.mint(rewardDistributor, toMint);
            if (cumulativeMinted == BUCKET_TOTAL) emit BucketExhausted(epochIndex);
        }

        delivered = toMint + treasuryPaid + feeShare;
        rec.amount = SafeCast.toUint128(delivered);
        emit EpochEmitted(epochIndex, toMint, treasuryPaid, primary, yearIndexOf(epochIndex));
    }

    function _floor(uint256 epochIndex) private returns (uint256 toMint, uint256 treasuryPaid, uint256 feeShare) {
        if (address(feeTreasury) != address(0)) feeShare = feeTreasury.getProtocolFeeShareToRunners(epochIndex);
        if (feeShare >= MIN_EPOCH_EMISSION) return (0, 0, feeShare);

        uint256 gap = MIN_EPOCH_EMISSION - feeShare;
        if (address(feeTreasury) != address(0)) {
            treasuryPaid = _min(feeTreasury.drawFeeTreasury(epochIndex, gap, rewardDistributor), gap);
        }
        toMint = _min(gap - treasuryPaid, remainingBucketBalance());

        uint256 paid = feeShare + treasuryPaid + toMint;
        if (paid < MIN_EPOCH_EMISSION) emit FloorShortfallAlert(epochIndex, MIN_EPOCH_EMISSION, paid);
    }

    function _min(uint256 a, uint256 b) private pure returns (uint256) {
        return a < b ? a : b;
    }
}
