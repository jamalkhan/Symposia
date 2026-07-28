// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title IStorageIntegrityOracle
/// @notice Issue #82 Stage 3/Stage 4 immediate-bypass trigger boundary: the
/// storage-layer redundancy/integrity oracle that confirms partial data loss
/// (redundancy for affected blob(s) dropped below the minimum replication
/// factor -- Stage 3 bypass) or permanent/unrecoverable data loss
/// (redundancy cannot be restored from any replica -- Stage 4 bypass). The
/// real replication-tracking/migration-mechanics implementation is a
/// separate, not-yet-built issue (out of scope per #82's own "Out of
/// scope" section) -- this interface only defines the call shape
/// `StoragePenaltyStateMachine.reportPartialDataLoss` /
/// `reportPermanentDataLoss` consume so that boundary can be wired to a
/// real implementation later without a contract change here.
interface IStorageIntegrityOracle {
    /// @notice Returns true if `node`'s redundancy for the blob(s)
    /// referenced by `evidenceRef` at `epoch` has been confirmed to have
    /// dropped below the minimum replication factor (recoverable from
    /// other replicas, not yet permanent loss).
    function confirmedPartialDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external view returns (bool);

    /// @notice Returns true if `node`'s data loss at `epoch`, referenced by
    /// `evidenceRef`, has been confirmed permanent/unrecoverable (redundancy
    /// cannot be restored from any replica).
    function confirmedPermanentDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external view returns (bool);
}
