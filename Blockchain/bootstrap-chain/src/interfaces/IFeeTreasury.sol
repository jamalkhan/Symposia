// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @notice Interface the post-primary emission floor (#73) expects from the
/// protocol fee splitter / 5% fee treasury (#86). Anticipated per #73's
/// Arch plan; #86 must implement it.
interface IFeeTreasury {
    /// @notice Protocol fee share routed to runners for `epochIndex` (delivered by #86, not minted).
    function getProtocolFeeShareToRunners(uint256 epochIndex) external view returns (uint256);

    /// @notice Transfers up to `requested` SYM from the fee treasury to
    /// `recipient`. Must not revert on shortfall: returns the amount actually paid.
    function drawFeeTreasury(uint256 epochIndex, uint256 requested, address recipient) external returns (uint256 paid);
}
