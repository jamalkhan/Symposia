// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title IProtocolConfig
/// @notice Governance/config storage read interface consumed (not owned) by the
/// core protocol contracts implemented for issue #52 (NodeRegistry-equivalent
/// staking registry, BlobDeals, RewardDistributor, SlashingController).
///
/// Per FR-1 of issue #52's spec, every economic/network parameter used by
/// those contracts MUST be read through this interface at call time rather
/// than baked into constructor arguments or contract-local storage the
/// contract itself writes. The concrete backing implementation (proposal /
/// voting / time-lock mechanics) is formalized in issue #54; until then,
/// `MockProtocolConfig` in this package is a minimal owner-settable stub
/// used for local development and testing.
///
/// Keys are namespaced `bytes32` values, conventionally
/// `keccak256("area.subarea.name")`, so the key space is self-documenting
/// and collision-resistant as it grows (see Arch pass on issue #52).
interface IProtocolConfig {
    /// @notice Returns the uint256 value stored for `key`, or 0 if unset.
    function getUint(bytes32 key) external view returns (uint256);

    /// @notice Returns the address stored for `key`, or address(0) if unset.
    function getAddress(bytes32 key) external view returns (address);

    /// @notice Returns the bool stored for `key`, or false if unset.
    function getBool(bytes32 key) external view returns (bool);
}
