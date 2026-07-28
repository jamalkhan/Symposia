// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title IComputeFailureOracle
/// @notice Issue #91 Stage 3 immediate-bypass trigger boundary: "a hosted
/// database becoming unavailable due to confirmed compute node failure."
/// The real detection/consensus mechanism is out of scope for this issue --
/// this interface only defines the call shape
/// `ComputePenaltyStateMachine.reportDatabaseUnavailable` consumes.
interface IComputeFailureOracle {
    /// @notice Returns true if a hosted database's unavailability at
    /// `epoch` has been confirmed attributable to `node`'s compute failure,
    /// keyed by `evidenceRef`.
    function confirmedNodeFailureUnavailability(address node, uint256 epoch, bytes32 evidenceRef)
        external
        view
        returns (bool);
}
