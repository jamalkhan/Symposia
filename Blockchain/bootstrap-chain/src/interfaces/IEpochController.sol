// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IEpochClock} from "./IEpochClock.sol";

/// @notice Epoch timing and finality surface (issue #72) consumed by the
/// reward distributor, telemetry checkpoints, emission schedule and penalty
/// contracts. See EpochController for the exact boundary rules.
interface IEpochController is IEpochClock {
    function currentEpoch() external view returns (uint256);
    function epochAt(uint256 timestamp) external view returns (uint256);
    function epochStart(uint256 epoch) external view returns (uint256);
    function epochEnd(uint256 epoch) external view returns (uint256);
    function finalityDeadline(uint256 epoch) external view returns (uint256);
    function acceptsReports(uint256 epoch) external view returns (bool);
    function isWithinFinalityWindow(uint256 epoch) external view returns (bool);
    function isSealed(uint256 epoch) external view returns (bool);
    function trySeal(uint256 epoch) external returns (bool);
}
