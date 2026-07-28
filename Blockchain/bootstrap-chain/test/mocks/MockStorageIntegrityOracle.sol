// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IStorageIntegrityOracle} from "../../src/interfaces/IStorageIntegrityOracle.sol";

/// @notice Minimal settable-by-anyone `IStorageIntegrityOracle` stub for
/// `StoragePenaltyStateMachine` test use only (issue #82). The real
/// replication-tracking/migration-mechanics oracle is a separate,
/// not-yet-built issue -- this just lets tests flip a confirmed/unconfirmed
/// bit per (node, epoch, evidenceRef) for each severity independently.
contract MockStorageIntegrityOracle is IStorageIntegrityOracle {
    mapping(bytes32 => bool) private _partialConfirmed;
    mapping(bytes32 => bool) private _permanentConfirmed;

    function setPartialConfirmed(address node, uint256 epoch, bytes32 evidenceRef, bool value) external {
        _partialConfirmed[keccak256(abi.encode(node, epoch, evidenceRef))] = value;
    }

    function setPermanentConfirmed(address node, uint256 epoch, bytes32 evidenceRef, bool value) external {
        _permanentConfirmed[keccak256(abi.encode(node, epoch, evidenceRef))] = value;
    }

    function confirmedPartialDataLoss(address node, uint256 epoch, bytes32 evidenceRef)
        external
        view
        returns (bool)
    {
        return _partialConfirmed[keccak256(abi.encode(node, epoch, evidenceRef))];
    }

    function confirmedPermanentDataLoss(address node, uint256 epoch, bytes32 evidenceRef)
        external
        view
        returns (bool)
    {
        return _permanentConfirmed[keccak256(abi.encode(node, epoch, evidenceRef))];
    }
}
