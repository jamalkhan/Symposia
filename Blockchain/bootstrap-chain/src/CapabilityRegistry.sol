// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ConsentRegistry} from "./ConsentRegistry.sol";

/// @title CapabilityRegistry
/// @notice Wallet-based Symposia identity root (issue #21). Issues
/// data-sharing capability tokens for marketers, re-validating an active
/// on-chain consent grant at mint time (FR5) so the "cannot issue without a
/// valid grant" guarantee is enforced structurally, not by an application-
/// layer gatekeeper that a future bug could bypass (TC-4.5, TC-4.6).
///
/// Non-upgradeable by design, matching ConsentRegistry and NodeRegistry.
contract CapabilityRegistry {
    struct CapabilityToken {
        address wallet;
        bytes32 tenantId;
        ConsentRegistry.Permission permission;
        uint64 issuedAt;
        uint64 consentGrantedAt;
    }

    ConsentRegistry public immutable consentRegistry;

    uint256 private _nextTokenId;
    mapping(uint256 => CapabilityToken) private _tokens;

    event CapabilityIssued(
        uint256 indexed tokenId,
        address indexed wallet,
        bytes32 indexed tenantId,
        ConsentRegistry.Permission permission,
        uint64 issuedAt,
        uint64 consentGrantedAt
    );

    constructor(ConsentRegistry consentRegistry_) {
        consentRegistry = consentRegistry_;
    }

    /// @notice Issues a capability token scoped to `wallet`/`tenantId`/
    /// `permission`, provided an active consent grant exists for that exact
    /// tuple (AC4). Reverts otherwise — there is no path to mint a token
    /// without going through this check (TC-4.2, TC-4.3, TC-4.4).
    function issueCapability(address wallet, bytes32 tenantId, ConsentRegistry.Permission permission)
        external
        returns (uint256 tokenId)
    {
        (bool granted, uint64 grantedAt,,) = consentRegistry.consentState(wallet, tenantId, permission);
        require(granted, "CapabilityRegistry: no active consent");

        tokenId = _nextTokenId++;
        uint64 issuedAt = uint64(block.timestamp);
        _tokens[tokenId] =
            CapabilityToken({wallet: wallet, tenantId: tenantId, permission: permission, issuedAt: issuedAt, consentGrantedAt: grantedAt});

        emit CapabilityIssued(tokenId, wallet, tenantId, permission, issuedAt, grantedAt);
    }

    /// @notice Returns the recorded token, including the originating consent
    /// grant's timestamp so a caller can trace it back on-chain (AC4).
    function getCapability(uint256 tokenId) external view returns (CapabilityToken memory) {
        return _tokens[tokenId];
    }

    function totalIssued() external view returns (uint256) {
        return _nextTokenId;
    }
}
