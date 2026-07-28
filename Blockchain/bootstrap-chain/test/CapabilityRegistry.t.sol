// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {ConsentRegistry} from "../src/ConsentRegistry.sol";
import {CapabilityRegistry} from "../src/CapabilityRegistry.sol";

contract CapabilityRegistryTest is Test {
    ConsentRegistry consentRegistry;
    CapabilityRegistry capabilityRegistry;

    uint256 walletAKey = 0xA11CE;
    address walletA;

    bytes32 tenantId = keccak256("tenant_01abc");
    bytes32 otherTenantId = keccak256("tenant_02xyz");
    bytes32 grantSourceHash = keccak256("checkout_form");
    bytes32 grantWordingHash = keccak256("I agree to receive marketing emails.");

    function setUp() public {
        consentRegistry = new ConsentRegistry();
        capabilityRegistry = new CapabilityRegistry(consentRegistry);
        walletA = vm.addr(walletAKey);
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

    function _domainSeparator() internal view returns (bytes32) {
        return keccak256(
            abi.encode(
                keccak256("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"),
                keccak256(bytes("Symposia.ConsentRegistry")),
                keccak256(bytes("1")),
                block.chainid,
                address(consentRegistry)
            )
        );
    }

    function _grant(ConsentRegistry.Permission[] memory perms, bytes32 tenant, uint256 nonce) internal {
        uint256 deadline = block.timestamp + 1 hours;
        bytes32 structHash = keccak256(
            abi.encode(
                keccak256(
                    "GrantConsent(address wallet,bytes32 tenantId,uint8[] permissions,bytes32 grantSourceHash,bytes32 grantWordingHash,uint256 nonce,uint256 deadline)"
                ),
                walletA,
                tenant,
                _encodePermissions(perms),
                grantSourceHash,
                grantWordingHash,
                nonce,
                deadline
            )
        );
        bytes32 digest = keccak256(abi.encodePacked("\x19\x01", _domainSeparator(), structHash));
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(walletAKey, digest);
        consentRegistry.grantConsent(walletA, tenant, perms, grantSourceHash, grantWordingHash, nonce, deadline, abi.encodePacked(r, s, v));
    }

    // TC-4.1 / AC4 / Gherkin "Capability token issuance follows a valid consent grant".
    function test_IssueCapability_WithActiveConsent_Succeeds() public {
        _grant(_permissions(ConsentRegistry.Permission.EmailMarketing), tenantId, 0);

        uint256 tokenId = capabilityRegistry.issueCapability(walletA, tenantId, ConsentRegistry.Permission.EmailMarketing);

        CapabilityRegistry.CapabilityToken memory token = capabilityRegistry.getCapability(tokenId);
        assertEq(token.wallet, walletA);
        assertEq(token.tenantId, tenantId);
        assertTrue(token.permission == ConsentRegistry.Permission.EmailMarketing);
        assertEq(token.consentGrantedAt, uint64(block.timestamp));
    }

    // TC-4.2: no consent grant exists at all => rejected.
    function test_IssueCapability_NoConsent_Reverts() public {
        vm.expectRevert("CapabilityRegistry: no active consent");
        capabilityRegistry.issueCapability(walletA, tenantId, ConsentRegistry.Permission.EmailMarketing);
    }

    // TC-4.3: consent granted to one tenant does not authorize a capability for another tenant.
    function test_IssueCapability_DifferentTenant_Reverts() public {
        _grant(_permissions(ConsentRegistry.Permission.EmailMarketing), tenantId, 0);

        vm.expectRevert("CapabilityRegistry: no active consent");
        capabilityRegistry.issueCapability(walletA, otherTenantId, ConsentRegistry.Permission.EmailMarketing);
    }

    // TC-4.4: consent exists for a different permission than requested.
    function test_IssueCapability_DifferentPermission_Reverts() public {
        _grant(_permissions(ConsentRegistry.Permission.EmailMarketing), tenantId, 0);

        vm.expectRevert("CapabilityRegistry: no active consent");
        capabilityRegistry.issueCapability(walletA, tenantId, ConsentRegistry.Permission.SmsMarketing);
    }

    // TC-5.1: revoked permission rejects new capability requests for that scope.
    function test_IssueCapability_AfterRevocation_Reverts() public {
        ConsentRegistry.Permission[] memory perms = _permissions(ConsentRegistry.Permission.WebTrackingNetwork);
        _grant(perms, tenantId, 0);

        uint256 deadline = block.timestamp + 1 hours;
        bytes32 structHash = keccak256(
            abi.encode(
                keccak256("RevokeConsent(address wallet,bytes32 tenantId,uint8[] permissions,uint256 nonce,uint256 deadline)"),
                walletA,
                tenantId,
                _encodePermissions(perms),
                uint256(1),
                deadline
            )
        );
        bytes32 digest = keccak256(abi.encodePacked("\x19\x01", _domainSeparator(), structHash));
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(walletAKey, digest);
        consentRegistry.revokeConsent(walletA, tenantId, perms, 1, deadline, abi.encodePacked(r, s, v));

        vm.expectRevert("CapabilityRegistry: no active consent");
        capabilityRegistry.issueCapability(walletA, tenantId, ConsentRegistry.Permission.WebTrackingNetwork);
    }

    // TC-4.6: a capability token can only come from this contract's own accounting —
    // there is no setter other than issueCapability, so forging one is impossible;
    // confirm an unissued tokenId simply reads as empty/zeroed, not a spoofed record.
    function test_GetCapability_UnissuedTokenId_ReturnsZeroValue() public view {
        CapabilityRegistry.CapabilityToken memory token = capabilityRegistry.getCapability(999);
        assertEq(token.wallet, address(0));
        assertEq(token.issuedAt, 0);
    }

    function test_TotalIssued_TracksMintCount() public {
        _grant(_permissions(ConsentRegistry.Permission.EmailMarketing), tenantId, 0);
        capabilityRegistry.issueCapability(walletA, tenantId, ConsentRegistry.Permission.EmailMarketing);
        assertEq(capabilityRegistry.totalIssued(), 1);
    }
}
