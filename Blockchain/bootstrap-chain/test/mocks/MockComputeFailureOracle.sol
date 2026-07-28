// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IComputeFailureOracle} from "../../src/interfaces/IComputeFailureOracle.sol";

/// @notice Minimal settable-by-anyone `IComputeFailureOracle` stub for
/// `ComputePenaltyStateMachine` test use only (issue #91). Real
/// detection/consensus for "hosted database unavailable due to confirmed
/// compute node failure" is out of scope here.
contract MockComputeFailureOracle is IComputeFailureOracle {
    mapping(bytes32 => bool) private _confirmed;

    function setConfirmed(address node, uint256 epoch, bytes32 evidenceRef, bool value) external {
        _confirmed[keccak256(abi.encode(node, epoch, evidenceRef))] = value;
    }

    function confirmedNodeFailureUnavailability(address node, uint256 epoch, bytes32 evidenceRef)
        external
        view
        returns (bool)
    {
        return _confirmed[keccak256(abi.encode(node, epoch, evidenceRef))];
    }
}
