// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title IDataLossOracle
/// @notice Issue #91 Stage 4 trigger boundary. The real WAL-gap /
/// safekeeper-failure-without-recovery data-loss oracle is a separate,
/// not-yet-built issue -- this interface only defines the call shape
/// `ComputePenaltyStateMachine.reportDataLoss` consumes so that boundary can
/// be wired to a real implementation later without a contract change here.
interface IDataLossOracle {
    /// @notice Returns true if data loss on `node` at `epoch`, keyed by
    /// `evidenceRef` (e.g. a hash of the WAL-gap / safekeeper-failure
    /// evidence bundle), has been confirmed.
    function confirmedDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external view returns (bool);
}
