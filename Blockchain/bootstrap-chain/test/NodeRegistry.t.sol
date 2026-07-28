// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {NodeRegistry} from "../src/NodeRegistry.sol";

contract NodeRegistryTest is Test {
    NodeRegistry registry;

    uint256 nodeAKey = 0xA11CE;
    uint256 nodeBKey = 0xB0B;
    address nodeA;
    address nodeB;

    function setUp() public {
        registry = new NodeRegistry();
        nodeA = vm.addr(nodeAKey);
        nodeB = vm.addr(nodeBKey);
    }

    function _signRegister(uint256 privateKey, address node) internal view returns (bytes memory) {
        bytes32 structHash = keccak256(abi.encode(keccak256("Register(address node)"), node));
        bytes32 digest = _toTypedDataHash(structHash);
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(privateKey, digest);
        return abi.encodePacked(r, s, v);
    }

    function _toTypedDataHash(bytes32 structHash) internal view returns (bytes32) {
        bytes32 domainSeparator = keccak256(
            abi.encode(
                keccak256("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"),
                keccak256(bytes("Symposia.NodeRegistry")),
                keccak256(bytes("1")),
                block.chainid,
                address(registry)
            )
        );
        return keccak256(abi.encodePacked("\x19\x01", domainSeparator, structHash));
    }

    // TC-2.1 / Gherkin: fresh node registers.
    function test_RegisterValidSignature_Succeeds() public {
        bytes memory sig = _signRegister(nodeAKey, nodeA);
        registry.register(nodeA, sig);
        assertTrue(registry.isRegistered(nodeA));
    }

    // TC-2.5: two distinct nodes register independently, no collision.
    function test_RegisterTwoDistinctNodes_NoCollision() public {
        registry.register(nodeA, _signRegister(nodeAKey, nodeA));
        registry.register(nodeB, _signRegister(nodeBKey, nodeB));
        assertTrue(registry.isRegistered(nodeA));
        assertTrue(registry.isRegistered(nodeB));
    }

    // TC-2.4 / TC-7.1 (FR10, AC7): duplicate registration is a safe no-op.
    function test_DuplicateRegistration_IsIdempotentNoOp() public {
        bytes memory sig = _signRegister(nodeAKey, nodeA);
        registry.register(nodeA, sig);
        registry.register(nodeA, sig);
        assertTrue(registry.isRegistered(nodeA));
    }

    // TC-2.4 variant: retry does not require resubmitting the exact same
    // signature bytes to be recognized as already-registered.
    function test_DuplicateRegistrationEvenWithoutSignature_IsNoOp() public {
        registry.register(nodeA, _signRegister(nodeAKey, nodeA));
        // Already registered, so this call short-circuits before signature
        // verification and cannot revert even with a garbage signature.
        registry.register(nodeA, hex"00");
        assertTrue(registry.isRegistered(nodeA));
    }

    // Forgery: signature from a different private key than the claimed node.
    function test_RegisterWithWrongSigner_Reverts() public {
        bytes memory sig = _signRegister(nodeBKey, nodeA);
        vm.expectRevert("NodeRegistry: invalid signature");
        registry.register(nodeA, sig);
    }

    function test_UnregisteredNode_IsNotRegistered() public view {
        assertFalse(registry.isRegistered(nodeA));
    }
}
