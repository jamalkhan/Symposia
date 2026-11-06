// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @notice Mint entrypoint of SYMToken (#70) used by allowlisted minters.
interface ISYMMinter {
    function mint(address to, uint256 amount) external;
}
