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

    string constant ENDPOINT_A = "http://10.0.0.1:5180";
    string constant ENDPOINT_A2 = "http://10.0.0.2:5180";
    string constant ENDPOINT_B = "http://10.0.0.3:5180";

    event NodeRegistered(address indexed node, string endpoint);
    event NodeEndpointUpdated(address indexed node, string endpoint);

    function setUp() public {
        registry = new NodeRegistry();
        nodeA = vm.addr(nodeAKey);
        nodeB = vm.addr(nodeBKey);
    }

    function _signRegister(uint256 privateKey, address node, string memory endpoint, uint256 nonce)
        internal
        view
        returns (bytes memory)
    {
        bytes32 structHash = keccak256(
            abi.encode(
                keccak256("Register(address node,string endpoint,uint256 nonce)"),
                node,
                keccak256(bytes(endpoint)),
                nonce
            )
        );
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

    function _register(uint256 key, address node, string memory endpoint) internal {
        uint256 nonce = registry.nonceOf(node);
        registry.register(node, endpoint, nonce, _signRegister(key, node, endpoint, nonce));
    }

    // #129 TC-6: valid registration stores endpoint, emits event, enumerable.
    function test_RegisterValidSignature_StoresEndpointAndEnumerates() public {
        vm.expectEmit(true, false, false, true);
        emit NodeRegistered(nodeA, ENDPOINT_A);
        _register(nodeAKey, nodeA, ENDPOINT_A);

        assertTrue(registry.isRegistered(nodeA));
        assertEq(registry.endpointOf(nodeA), ENDPOINT_A);
        assertEq(registry.nonceOf(nodeA), 1);
        assertEq(registry.nodeCount(), 1);
        assertEq(registry.nodeAt(0), nodeA);
    }

    // #110 TC-2.5: two distinct nodes register independently, no collision.
    function test_RegisterTwoDistinctNodes_NoCollision() public {
        _register(nodeAKey, nodeA, ENDPOINT_A);
        _register(nodeBKey, nodeB, ENDPOINT_B);
        assertTrue(registry.isRegistered(nodeA));
        assertTrue(registry.isRegistered(nodeB));
        assertEq(registry.endpointOf(nodeB), ENDPOINT_B);
        assertEq(registry.nodeCount(), 2);
    }

    // #110 FR10: duplicate registration with the same endpoint is a safe no-op.
    function test_DuplicateRegistrationSameEndpoint_IsIdempotentNoOp() public {
        bytes memory sig = _signRegister(nodeAKey, nodeA, ENDPOINT_A, 0);
        registry.register(nodeA, ENDPOINT_A, 0, sig);
        registry.register(nodeA, ENDPOINT_A, 0, sig);
        assertEq(registry.nonceOf(nodeA), 1);
        assertEq(registry.nodeCount(), 1);
    }

    // #110 variant: retry short-circuits before signature verification.
    function test_DuplicateRegistrationEvenWithoutSignature_IsNoOp() public {
        _register(nodeAKey, nodeA, ENDPOINT_A);
        registry.register(nodeA, ENDPOINT_A, 0, hex"00");
        assertTrue(registry.isRegistered(nodeA));
    }

    // #129 TC-9: endpoint update with the next nonce.
    function test_EndpointUpdate_WithNextNonce_Updates() public {
        _register(nodeAKey, nodeA, ENDPOINT_A);

        vm.expectEmit(true, false, false, true);
        emit NodeEndpointUpdated(nodeA, ENDPOINT_A2);
        _register(nodeAKey, nodeA, ENDPOINT_A2);

        assertEq(registry.endpointOf(nodeA), ENDPOINT_A2);
        assertEq(registry.nonceOf(nodeA), 2);
        assertEq(registry.nodeCount(), 1);
    }

    // #129 TC-7: replaying an old signature cannot roll the endpoint back.
    function test_ReplayedOldSignature_Reverts() public {
        bytes memory oldSig = _signRegister(nodeAKey, nodeA, ENDPOINT_A, 0);
        registry.register(nodeA, ENDPOINT_A, 0, oldSig);
        _register(nodeAKey, nodeA, ENDPOINT_A2);

        vm.expectRevert("NodeRegistry: invalid nonce");
        registry.register(nodeA, ENDPOINT_A, 0, oldSig);
    }

    // #129 TC-7: signature from a different private key than the claimed node.
    function test_RegisterWithWrongSigner_Reverts() public {
        bytes memory sig = _signRegister(nodeBKey, nodeA, ENDPOINT_A, 0);
        vm.expectRevert("NodeRegistry: invalid signature");
        registry.register(nodeA, ENDPOINT_A, 0, sig);
    }

    // #129 TC-7 / Gherkin: endpoint not signed by the node is rejected.
    function test_RegisterWithTamperedEndpoint_Reverts() public {
        bytes memory sig = _signRegister(nodeAKey, nodeA, ENDPOINT_A, 0);
        vm.expectRevert("NodeRegistry: invalid signature");
        registry.register(nodeA, "http://attacker:5180", 0, sig);
    }

    // #129 TC-8: empty or oversized endpoint.
    function test_EmptyEndpoint_Reverts() public {
        vm.expectRevert("NodeRegistry: invalid endpoint");
        registry.register(nodeA, "", 0, hex"00");
    }

    function test_OversizedEndpoint_Reverts() public {
        bytes memory big = new bytes(257);
        for (uint256 i = 0; i < big.length; i++) {
            big[i] = "a";
        }
        vm.expectRevert("NodeRegistry: invalid endpoint");
        registry.register(nodeA, string(big), 0, hex"00");
    }

    function test_UnregisteredNode_IsNotRegistered() public view {
        assertFalse(registry.isRegistered(nodeA));
        assertEq(registry.endpointOf(nodeA), "");
    }
}
