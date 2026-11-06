// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {IEpochController} from "./interfaces/IEpochController.sol";

/// @title EpochController
/// @notice The protocol's single source of truth for epoch timing (issue #72,
/// tokenomics-mvp.md §4). Epochs are UTC-anchored to `genesisTimestamp` and
/// numbered from 0. Every reward, penalty and telemetry contract reads epoch
/// boundaries and finality from here instead of doing its own math.
///
/// Timing rules (pinned by tests):
/// - Epoch N covers `[epochStart(N), epochEnd(N))`; `epochEnd(N) == epochStart(N + 1)`.
/// - Reports for epoch N are accepted from `epochStart(N)` up to and including
///   `finalityDeadline(N) = epochEnd(N) + finalityWindow`. After that they are
///   rejected, never rolled into N + 1 (a node must not be able to pick its
///   epoch by timing a late submission).
/// - Epoch N is sealed from `finalityDeadline(N) + 1` on. `isSealed` is purely
///   time-based, so it never depends on anyone having called `trySeal`;
///   `trySeal` just records it and emits `EpochSealed` for indexers. Repeat
///   calls are no-ops. Epochs can be sealed in any order.
/// - Timestamps before genesis revert with `BeforeGenesis`.
///
/// Epoch length is a Tier 3 governance parameter. A change appends a
/// checkpoint that takes effect at the next epoch boundary, so it never moves
/// a past or in-progress epoch. With no changes (the expected case) the math
/// is the spec's `floor((t - genesisTimestamp) / length)`. The checkpoint list
/// is public, so anyone can recompute any epoch off-chain.
///
/// Deliberately not upgradeable: nothing can rewrite a past epoch's
/// boundaries. Governance is the config's `GOVERNANCE_TIMELOCK`, resolved on
/// each call so it follows timelock rotation.
contract EpochController is IEpochController {
    struct LengthCheckpoint {
        uint64 effectiveEpoch; // first epoch this length applies to
        uint64 effectiveTimestamp; // epochStart(effectiveEpoch)
        uint32 length; // seconds
    }

    uint32 public constant MIN_EPOCH_LENGTH = 1 hours;
    uint32 public constant MAX_FINALITY_WINDOW = 1 days;

    IProtocolConfig public immutable config;
    uint64 public immutable genesisTimestamp;
    uint32 public immutable finalityWindow;

    LengthCheckpoint[] private _checkpoints;
    mapping(uint256 => bool) private _recordedSeals;

    event EpochSealed(uint256 indexed epoch, uint256 start, uint256 end);
    event EpochLengthChanged(uint256 indexed effectiveEpoch, uint256 effectiveTimestamp, uint256 newLength);

    error ZeroAddress();
    error InvalidEpochLength(uint256 length);
    error InvalidFinalityWindow(uint256 window);
    error BeforeGenesis(uint256 timestamp, uint256 genesis);
    error NotGovernance(address caller);
    error FinalityWindowOpen(uint256 epoch, uint256 deadline);

    constructor(IProtocolConfig config_, uint64 genesisTimestamp_, uint32 epochLength_, uint32 finalityWindow_) {
        if (address(config_) == address(0)) revert ZeroAddress();
        if (epochLength_ < MIN_EPOCH_LENGTH) revert InvalidEpochLength(epochLength_);
        if (finalityWindow_ == 0 || finalityWindow_ > MAX_FINALITY_WINDOW || finalityWindow_ >= epochLength_) {
            revert InvalidFinalityWindow(finalityWindow_);
        }
        config = config_;
        genesisTimestamp = genesisTimestamp_;
        finalityWindow = finalityWindow_;
        _checkpoints.push(LengthCheckpoint(0, genesisTimestamp_, epochLength_));
        emit EpochLengthChanged(0, genesisTimestamp_, epochLength_);
    }

    // ---------------------------------------------------------------------
    // Epoch math
    // ---------------------------------------------------------------------

    function currentEpoch() external view returns (uint256) {
        return epochAt(block.timestamp);
    }

    function epochAt(uint256 timestamp) public view returns (uint256) {
        if (timestamp < genesisTimestamp) revert BeforeGenesis(timestamp, genesisTimestamp);
        uint256 i = _checkpoints.length;
        while (i > 1 && _checkpoints[i - 1].effectiveTimestamp > timestamp) --i;
        LengthCheckpoint memory cp = _checkpoints[i - 1];
        return cp.effectiveEpoch + (timestamp - cp.effectiveTimestamp) / cp.length;
    }

    function epochStart(uint256 epoch) public view returns (uint256) {
        uint256 i = _checkpoints.length;
        while (i > 1 && _checkpoints[i - 1].effectiveEpoch > epoch) --i;
        LengthCheckpoint memory cp = _checkpoints[i - 1];
        return cp.effectiveTimestamp + (epoch - cp.effectiveEpoch) * cp.length;
    }

    /// @notice Exclusive end of `epoch` (the start of `epoch + 1`).
    function epochEnd(uint256 epoch) public view returns (uint256) {
        return epochStart(epoch + 1);
    }

    /// @notice IEpochClock (#73).
    function epochEndTimestamp(uint256 epoch) external view returns (uint256) {
        return epochStart(epoch + 1);
    }

    /// @notice Last second (inclusive) at which reports for `epoch` are accepted.
    function finalityDeadline(uint256 epoch) public view returns (uint256) {
        return epochStart(epoch + 1) + finalityWindow;
    }

    /// @notice Whether a report addressed to `epoch` may be accepted now: the
    /// epoch has started and its finality deadline hasn't passed.
    function acceptsReports(uint256 epoch) public view returns (bool) {
        return block.timestamp >= epochStart(epoch) && block.timestamp <= finalityDeadline(epoch);
    }

    /// @notice Whether `epoch` has ended and is still inside its finality window.
    function isWithinFinalityWindow(uint256 epoch) external view returns (bool) {
        return block.timestamp >= epochStart(epoch + 1) && block.timestamp <= finalityDeadline(epoch);
    }

    function isSealed(uint256 epoch) public view returns (bool) {
        return _recordedSeals[epoch] || block.timestamp > finalityDeadline(epoch);
    }

    /// @notice Records that `epoch` is sealed and emits `EpochSealed` once.
    /// Permissionless; reverts only while the finality window is still open.
    function trySeal(uint256 epoch) external returns (bool) {
        if (_recordedSeals[epoch]) return true;
        uint256 deadline = finalityDeadline(epoch);
        if (block.timestamp <= deadline) revert FinalityWindowOpen(epoch, deadline);
        _recordedSeals[epoch] = true;
        emit EpochSealed(epoch, epochStart(epoch), epochStart(epoch + 1));
        return true;
    }

    // ---------------------------------------------------------------------
    // Governance (Tier 3)
    // ---------------------------------------------------------------------

    /// @notice Current epoch length (the length of the most recent checkpoint,
    /// which may not have taken effect yet).
    function epochLength() external view returns (uint256) {
        return _checkpoints[_checkpoints.length - 1].length;
    }

    function checkpointCount() external view returns (uint256) {
        return _checkpoints.length;
    }

    function checkpointAt(uint256 index) external view returns (LengthCheckpoint memory) {
        return _checkpoints[index];
    }

    /// @notice Sets the epoch length from the next epoch boundary on. Calling
    /// again before that boundary replaces the pending (not yet effective)
    /// change; effective checkpoints are never modified.
    function setEpochLength(uint32 newLength) external {
        if (msg.sender != config.getAddress(ConfigKeys.GOVERNANCE_TIMELOCK)) revert NotGovernance(msg.sender);
        if (newLength < MIN_EPOCH_LENGTH || finalityWindow >= newLength) revert InvalidEpochLength(newLength);

        uint256 next = epochAt(block.timestamp) + 1;
        LengthCheckpoint storage last = _checkpoints[_checkpoints.length - 1];
        if (last.effectiveEpoch == next) {
            last.length = newLength; // still pending: its start time doesn't change
        } else {
            _checkpoints.push(LengthCheckpoint(uint64(next), uint64(epochStart(next)), newLength));
        }
        emit EpochLengthChanged(next, epochStart(next), newLength);
    }
}
