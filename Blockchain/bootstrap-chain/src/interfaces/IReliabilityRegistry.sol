// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @notice What RewardDistributor (#80) needs from the reliability registry (#79).
interface IReliabilityRegistry {
    /// @notice Closes `epoch` for `node` and returns its reliability score
    /// (bps, 1.0 = 10_000) and whether it earns the trailing-window bonus.
    function finalizeEpoch(address node, uint256 epoch, uint256 heartbeatBps)
        external
        returns (uint256 scoreBps, bool bonusEligible);

    /// @notice Score and bonus eligibility for an already-finalized (node, epoch).
    function reliabilityOf(address node, uint256 epoch) external view returns (uint256 scoreBps, bool bonusEligible);
}
