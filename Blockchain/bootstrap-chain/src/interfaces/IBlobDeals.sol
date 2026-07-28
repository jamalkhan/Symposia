// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title IBlobDeals
/// @notice Narrow read interface exposing deal replica/region data to the
/// reward and slashing contracts (and off-chain verifiers), per FR-3.
interface IBlobDeals {
    function replicasOf(bytes32 dealId) external view returns (address[] memory);
    function regionOf(bytes32 dealId) external view returns (bytes32);
}
