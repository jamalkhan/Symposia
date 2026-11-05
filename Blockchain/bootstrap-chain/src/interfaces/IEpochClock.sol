// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @notice Minimal epoch-timing view consumed by NetworkRewardsEmission
/// (#73). Implemented by RewardDistributor today; the #72 epoch controller
/// can implement it later without changing consumers.
interface IEpochClock {
    /// @notice Unix timestamp at which `epochIndex` ends (exclusive).
    function epochEndTimestamp(uint256 epochIndex) external view returns (uint256);
}
