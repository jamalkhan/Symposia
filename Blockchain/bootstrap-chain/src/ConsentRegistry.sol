// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {EIP712} from "@openzeppelin/contracts/utils/cryptography/EIP712.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";

/// @title ConsentRegistry
/// @notice Wallet-based Symposia identity root (issue #21). Records consent
/// grants and revocations for the Symposia permission model
/// (Requirements/Identity/user-data-ownership.md), keyed to a wallet address.
/// A consent grant/revocation is only recordable with a valid EIP-712
/// signature from the wallet it claims to originate from (FR6) — this is the
/// enforcement point for that guarantee, not application code, so it can't be
/// bypassed by a service bug or an internal actor.
///
/// Non-upgradeable by design, matching NodeRegistry (issue #110): consent
/// state is the crux of the platform's "structural, not policy" promise, so
/// no admin key should be able to alter it. Evolving the logic later means a
/// new contract version and an explicit migration.
contract ConsentRegistry is EIP712 {
    /// @notice The Symposia marketer permission model
    /// (Requirements/Identity/user-data-ownership.md § Marketer Permission Types).
    enum Permission {
        EmailMarketing,
        EmailTransactional,
        SmsMarketing,
        WebTrackingBrand,
        WebTrackingNetwork,
        DataRead,
        DataEnrichment
    }

    struct ConsentState {
        bool granted;
        uint64 grantedAt;
        bytes32 grantSourceHash;
        bytes32 grantWordingHash;
    }

    bytes32 private constant GRANT_TYPEHASH = keccak256(
        "GrantConsent(address wallet,bytes32 tenantId,uint8[] permissions,bytes32 grantSourceHash,bytes32 grantWordingHash,uint256 nonce,uint256 deadline)"
    );

    bytes32 private constant REVOKE_TYPEHASH =
        keccak256("RevokeConsent(address wallet,bytes32 tenantId,uint8[] permissions,uint256 nonce,uint256 deadline)");

    /// wallet => tenantId => permission => state
    mapping(address => mapping(bytes32 => mapping(Permission => ConsentState))) private _consent;

    /// wallet => next expected nonce, for replay protection (TC-2.4).
    mapping(address => uint256) public nonces;

    event ConsentGranted(
        address indexed wallet,
        bytes32 indexed tenantId,
        Permission[] permissions,
        uint64 grantedAt,
        bytes32 grantSourceHash,
        bytes32 grantWordingHash
    );

    event ConsentRevoked(address indexed wallet, bytes32 indexed tenantId, Permission[] permissions, uint64 revokedAt);

    constructor() EIP712("Symposia.ConsentRegistry", "1") {}

    /// @notice Records a consent grant for `wallet`/`tenantId` covering
    /// `permissions`, authenticated by an EIP-712 signature from `wallet`
    /// (FR6, AC3). A relayer (the Identity Gateway) may submit this
    /// transaction on the wallet's behalf; the wallet's authority is proven
    /// by the signature, not by `msg.sender`.
    function grantConsent(
        address wallet,
        bytes32 tenantId,
        Permission[] calldata permissions,
        bytes32 grantSourceHash,
        bytes32 grantWordingHash,
        uint256 nonce,
        uint256 deadline,
        bytes calldata signature
    ) external {
        require(permissions.length > 0, "ConsentRegistry: no permissions");
        require(block.timestamp <= deadline, "ConsentRegistry: expired");
        require(nonce == nonces[wallet], "ConsentRegistry: bad nonce");

        bytes32 structHash = keccak256(
            abi.encode(
                GRANT_TYPEHASH,
                wallet,
                tenantId,
                _encodePermissions(permissions),
                grantSourceHash,
                grantWordingHash,
                nonce,
                deadline
            )
        );
        _verify(wallet, structHash, signature);
        nonces[wallet] = nonce + 1;

        uint64 grantedAt = uint64(block.timestamp);
        for (uint256 i = 0; i < permissions.length; i++) {
            _consent[wallet][tenantId][permissions[i]] =
                ConsentState({granted: true, grantedAt: grantedAt, grantSourceHash: grantSourceHash, grantWordingHash: grantWordingHash});
        }

        emit ConsentGranted(wallet, tenantId, permissions, grantedAt, grantSourceHash, grantWordingHash);
    }

    /// @notice Revokes previously-granted `permissions` for `wallet`/`tenantId`
    /// (Gherkin: "Revocation invalidates future capability issuance").
    /// Requires the same wallet-signature proof as granting (symmetry noted
    /// as an open question in the QA plan; resolved here as required).
    /// Revoking a permission that was never granted is a safe no-op (TC-5.3).
    function revokeConsent(
        address wallet,
        bytes32 tenantId,
        Permission[] calldata permissions,
        uint256 nonce,
        uint256 deadline,
        bytes calldata signature
    ) external {
        require(permissions.length > 0, "ConsentRegistry: no permissions");
        require(block.timestamp <= deadline, "ConsentRegistry: expired");
        require(nonce == nonces[wallet], "ConsentRegistry: bad nonce");

        bytes32 structHash =
            keccak256(abi.encode(REVOKE_TYPEHASH, wallet, tenantId, _encodePermissions(permissions), nonce, deadline));
        _verify(wallet, structHash, signature);
        nonces[wallet] = nonce + 1;

        uint64 revokedAt = uint64(block.timestamp);
        for (uint256 i = 0; i < permissions.length; i++) {
            _consent[wallet][tenantId][permissions[i]].granted = false;
        }

        emit ConsentRevoked(wallet, tenantId, permissions, revokedAt);
    }

    /// @notice Returns whether `wallet` currently has an active (granted,
    /// non-revoked) consent for `permission` scoped to `tenantId`. This is the
    /// authoritative check `CapabilityRegistry` re-validates at mint time.
    function hasActiveConsent(address wallet, bytes32 tenantId, Permission permission) external view returns (bool) {
        return _consent[wallet][tenantId][permission].granted;
    }

    /// @notice Returns the full recorded state for a wallet/tenant/permission,
    /// used by capability issuance to trace a token back to its originating
    /// grant (AC4: "traceable on-chain to the originating consent grant").
    function consentState(address wallet, bytes32 tenantId, Permission permission)
        external
        view
        returns (bool granted, uint64 grantedAt, bytes32 grantSourceHash, bytes32 grantWordingHash)
    {
        ConsentState storage state = _consent[wallet][tenantId][permission];
        return (state.granted, state.grantedAt, state.grantSourceHash, state.grantWordingHash);
    }

    function _verify(address wallet, bytes32 structHash, bytes calldata signature) private view {
        bytes32 digest = _hashTypedDataV4(structHash);
        address signer = ECDSA.recover(digest, signature);
        require(signer == wallet, "ConsentRegistry: invalid signature");
    }

    function _encodePermissions(Permission[] calldata permissions) private pure returns (bytes32) {
        uint8[] memory raw = new uint8[](permissions.length);
        for (uint256 i = 0; i < permissions.length; i++) {
            raw[i] = uint8(permissions[i]);
        }
        return keccak256(abi.encodePacked(raw));
    }
}
