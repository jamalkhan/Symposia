// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @notice What RewardDistributor needs from the emission contract (#73).
interface INetworkRewardsEmission {
    /// @notice Idempotently delivers `epochIndex`'s emission to the
    /// distributor and returns the total SYM delivered for that epoch.
    function emitForEpoch(uint256 epochIndex) external returns (uint256 delivered);
}
