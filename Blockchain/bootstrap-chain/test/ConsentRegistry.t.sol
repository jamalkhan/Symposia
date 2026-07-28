// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {ConsentRegistry} from "../src/ConsentRegistry.sol";

contract ConsentRegistryTest is Test {
    ConsentRegistry registry;

    uint256 walletAKey = 0xA11CE;
    uint256 walletBKey = 0xB0B;
    address walletA;
    address walletB;

    bytes32 tenantId = keccak256("tenant_01abc");
    bytes32 grantSourceHash = keccak256("checkout_form");
    bytes32 grantWordingHash = keccak256("I agree to receive marketing emails.");

    function setUp() public {
        registry = new ConsentRegistry();
        walletA = vm.addr(walletAKey);
        walletB = vm.addr(walletBKey);
    }

    function _permissions(ConsentRegistry.Permission p) internal pure returns (ConsentRegistry.Permission[] memory arr) {
        arr = new ConsentRegistry.Permission[](1);
        arr[0] = p;
    }

    function _encodePermissions(ConsentRegistry.Permission[] memory permissions) internal pure returns (bytes32) {
        uint8[] memory raw = new uint8[](permissions.length);
        for (uint256 i = 0; i < permissions.length; i++) {
            raw[i] = uint8(permissions[i]);
        }
        return keccak256(abi.encodePacked(raw));
    }

    function _domainSeparator(string memory name) internal view returns (bytes32) {
        return keccak256(
            abi.encode(
                keccak256("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"),
                keccak256(bytes(name)),
                keccak256(bytes("1")),
                block.chainid,
                address(registry)
            )
        );
    }

    function _signGrant(
        uint256 privateKey,
        address wallet,
        ConsentRegistry.Permission[] memory permissions,
        uint256 nonce,
        uint256 deadline
    ) internal view returns (bytes memory) {
        bytes32 structHash = keccak256(
            abi.encode(
                keccak256(
                    "GrantConsent(address wallet,bytes32 tenantId,uint8[] permissions,bytes32 grantSourceHash,bytes32 grantWordingHash,uint256 nonce,uint256 deadline)"
                ),
                wallet,
                tenantId,
                _encodePermissions(permissions),
                grantSourceHash,
                grantWordingHash,
                nonce,
                deadline
            )
        );
        bytes32 digest = keccak256(abi.encodePacked("\x19\x01", _domainSeparator("Symposia.ConsentRegistry"), structHash));
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(privateKey, digest);
        return abi.encodePacked(r, s, v);
    }

    function _signRevoke(
        uint256 privateKey,
        address wallet,
        ConsentRegistry.Permission[] memory permissions,
        uint256 nonce,
        uint256 deadline
    ) internal view returns (bytes memory) {
        bytes32 structHash = keccak256(
            abi.encode(
                keccak256("RevokeConsent(address wallet,bytes32 tenantId,uint8[] permissions,uint256 nonce,uint256 deadline)"),
                wallet,
                tenantId,
                _encodePermissions(permissions),
                nonce,
                deadline
            )
        );
        bytes32 digest = keccak256(abi.encodePacked("\x19\x01", _domainSeparator("Symposia.ConsentRegistry"), structHash));
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(privateKey, digest);
        return abi.encodePacked(r, s, v);
    }

    // TC-3.1 / AC2: valid signed grant is recorded with the expected fields.
    function test_GrantConsent_ValidSignature_Recorded() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.EmailMarketing);
        uint256 deadline = block.timestamp + 1 hours;
        bytes memory sig = _signGrant(walletAKey, walletA, perms, 0, deadline);

        registry.grantConsent(walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, sig);

        assertTrue(registry.hasActiveConsent(walletA, tenantId, ConsentRegistry.Permission.EmailMarketing));
        (bool granted, uint64 grantedAt, bytes32 srcHash, bytes32 wordingHash) =
            registry.consentState(walletA, tenantId, ConsentRegistry.Permission.EmailMarketing);
        assertTrue(granted);
        assertEq(grantedAt, uint64(block.timestamp));
        assertEq(srcHash, grantSourceHash);
        assertEq(wordingHash, grantWordingHash);
    }

    // TC-3.6: every permission type in the model is accepted and recorded distinctly.
    function test_GrantConsent_AllPermissionTypes_Recorded() public {
        ConsentRegistry.Permission[7] memory all = [
            ConsentRegistry.Permission.EmailMarketing,
            ConsentRegistry.Permission.EmailTransactional,
            ConsentRegistry.Permission.SmsMarketing,
            ConsentRegistry.Permission.WebTrackingBrand,
            ConsentRegistry.Permission.WebTrackingNetwork,
            ConsentRegistry.Permission.DataRead,
            ConsentRegistry.Permission.DataEnrichment
        ];
        for (uint256 i = 0; i < all.length; i++) {
            ConsentRegistry.Permission[] memory perms = _permissions(all[i]);
            uint256 deadline = block.timestamp + 1 hours;
            bytes memory sig = _signGrant(walletAKey, walletA, perms, i, deadline);
            registry.grantConsent(walletA, tenantId, perms, grantSourceHash, grantWordingHash, i, deadline, sig);
            assertTrue(registry.hasActiveConsent(walletA, tenantId, all[i]));
        }
    }

    // TC-3.2 / Gherkin "Consent grant requires wallet signature": no signature => reverts.
    function test_GrantConsent_InvalidSignature_Reverts() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.EmailMarketing);
        uint256 deadline = block.timestamp + 1 hours;
        bytes memory garbage = new bytes(65);

        vm.expectRevert();
        registry.grantConsent(walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, garbage);
    }

    // TC-3.3: signature from a different wallet than the one claimed is rejected.
    function test_GrantConsent_WrongSigner_Reverts() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.EmailMarketing);
        uint256 deadline = block.timestamp + 1 hours;
        bytes memory sig = _signGrant(walletBKey, walletA, perms, 0, deadline);

        vm.expectRevert("ConsentRegistry: invalid signature");
        registry.grantConsent(walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, sig);
    }

    // TC-2.5 / TC-3.4: expired deadline is rejected.
    function test_GrantConsent_ExpiredDeadline_Reverts() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.EmailMarketing);
        uint256 deadline = block.timestamp;
        bytes memory sig = _signGrant(walletAKey, walletA, perms, 0, deadline);

        vm.warp(block.timestamp + 1);
        vm.expectRevert("ConsentRegistry: expired");
        registry.grantConsent(walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, sig);
    }

    // TC-2.4: replaying a previously-used valid signature is rejected (nonce reuse blocked).
    function test_GrantConsent_ReplayedSignature_Reverts() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.EmailMarketing);
        uint256 deadline = block.timestamp + 1 hours;
        bytes memory sig = _signGrant(walletAKey, walletA, perms, 0, deadline);

        registry.grantConsent(walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, sig);

        vm.expectRevert("ConsentRegistry: bad nonce");
        registry.grantConsent(walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, sig);
    }

    // TC-3.5: a single grant can cover multiple permissions at once.
    function test_GrantConsent_MultiplePermissions_AllRecorded() public {
        ConsentRegistry.Permission[] memory perms = new ConsentRegistry.Permission[](2);
        perms[0] = ConsentRegistry.Permission.EmailMarketing;
        perms[1] = ConsentRegistry.Permission.WebTrackingBrand;
        uint256 deadline = block.timestamp + 1 hours;
        bytes memory sig = _signGrant(walletAKey, walletA, perms, 0, deadline);

        registry.grantConsent(walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, sig);

        assertTrue(registry.hasActiveConsent(walletA, tenantId, ConsentRegistry.Permission.EmailMarketing));
        assertTrue(registry.hasActiveConsent(walletA, tenantId, ConsentRegistry.Permission.WebTrackingBrand));
    }

    // Gherkin "Revocation invalidates future capability issuance": revoke then check state.
    function test_RevokeConsent_ValidSignature_InvalidatesConsent() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.WebTrackingNetwork);
        uint256 deadline = block.timestamp + 1 hours;
        registry.grantConsent(
            walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, _signGrant(walletAKey, walletA, perms, 0, deadline)
        );

        bytes memory revokeSig = _signRevoke(walletAKey, walletA, perms, 1, deadline);
        registry.revokeConsent(walletA, tenantId, perms, 1, deadline, revokeSig);

        assertFalse(registry.hasActiveConsent(walletA, tenantId, ConsentRegistry.Permission.WebTrackingNetwork));
    }

    // TC-5.2: revoking one permission leaves other granted permissions intact.
    function test_RevokeConsent_ScopedToPermission_OthersUnaffected() public {
        ConsentRegistry.Permission[] memory perms = new ConsentRegistry.Permission[](2);
        perms[0] = ConsentRegistry.Permission.EmailMarketing;
        perms[1] = ConsentRegistry.Permission.WebTrackingNetwork;
        uint256 deadline = block.timestamp + 1 hours;
        registry.grantConsent(
            walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, _signGrant(walletAKey, walletA, perms, 0, deadline)
        );

        ConsentRegistry.Permission[] memory toRevoke = _permissions(ConsentRegistry.Permission.WebTrackingNetwork);
        registry.revokeConsent(walletA, tenantId, toRevoke, 1, deadline, _signRevoke(walletAKey, walletA, toRevoke, 1, deadline));

        assertTrue(registry.hasActiveConsent(walletA, tenantId, ConsentRegistry.Permission.EmailMarketing));
        assertFalse(registry.hasActiveConsent(walletA, tenantId, ConsentRegistry.Permission.WebTrackingNetwork));
    }

    // TC-5.6: revocation requires a valid wallet signature, symmetric with grants.
    function test_RevokeConsent_WrongSigner_Reverts() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.EmailMarketing);
        uint256 deadline = block.timestamp + 1 hours;
        registry.grantConsent(
            walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, _signGrant(walletAKey, walletA, perms, 0, deadline)
        );

        bytes memory badSig = _signRevoke(walletBKey, walletA, perms, 1, deadline);
        vm.expectRevert("ConsentRegistry: invalid signature");
        registry.revokeConsent(walletA, tenantId, perms, 1, deadline, badSig);
    }

    // TC-5.5: revoke, then re-grant the same permission — cycle is not terminal.
    function test_RevokeThenReGrant_Succeeds() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.EmailMarketing);
        uint256 deadline = block.timestamp + 1 hours;
        registry.grantConsent(
            walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, _signGrant(walletAKey, walletA, perms, 0, deadline)
        );
        registry.revokeConsent(walletA, tenantId, perms, 1, deadline, _signRevoke(walletAKey, walletA, perms, 1, deadline));
        assertFalse(registry.hasActiveConsent(walletA, tenantId, ConsentRegistry.Permission.EmailMarketing));

        registry.grantConsent(
            walletA, tenantId, perms, grantSourceHash, grantWordingHash, 2, deadline, _signGrant(walletAKey, walletA, perms, 2, deadline)
        );
        assertTrue(registry.hasActiveConsent(walletA, tenantId, ConsentRegistry.Permission.EmailMarketing));
    }

    // TC-4.3 analog at the registry level: tenant isolation — consent to one tenant
    // does not leak to another.
    function test_ConsentIsScopedPerTenant() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.EmailMarketing);
        uint256 deadline = block.timestamp + 1 hours;
        registry.grantConsent(
            walletA, tenantId, perms, grantSourceHash, grantWordingHash, 0, deadline, _signGrant(walletAKey, walletA, perms, 0, deadline)
        );

        bytes32 otherTenant = keccak256("tenant_02xyz");
        assertFalse(registry.hasActiveConsent(walletA, otherTenant, ConsentRegistry.Permission.EmailMarketing));
    }
}
