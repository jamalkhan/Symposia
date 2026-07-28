// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IDataLossOracle} from "../../src/interfaces/IDataLossOracle.sol";

/// @notice Minimal settable-by-anyone `IDataLossOracle` stub for
/// `ComputePenaltyStateMachine` test use only (issue #91). The real
/// WAL-gap / safekeeper-failure-without-recovery oracle is a separate,
/// not-yet-built issue -- this just lets tests flip a confirmed/unconfirmed
/// bit per (node, epoch, evidenceRef).
contract MockDataLossOracle is IDataLossOracle {
    mapping(bytes32 => bool) private _confirmed;

    function setConfirmed(address node, uint256 epoch, bytes32 evidenceRef, bool value) external {
        _confirmed[keccak256(abi.encode(node, epoch, evidenceRef))] = value;
    }

    function confirmedDataLoss(address node, uint256 epoch, bytes32 evidenceRef) external view returns (bool) {
        return _confirmed[keccak256(abi.encode(node, epoch, evidenceRef))];
    }
}
