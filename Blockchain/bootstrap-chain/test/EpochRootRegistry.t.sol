// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Test} from "forge-std/Test.sol";
import {NodeRegistry} from "../src/NodeRegistry.sol";
import {EpochRootRegistry} from "../src/EpochRootRegistry.sol";

contract EpochRootRegistryTest is Test {
    NodeRegistry nodeRegistry;
    EpochRootRegistry rootRegistry;

    uint256 nodeAKey = 0xA11CE;
    uint256 nodeBKey = 0xB0B;
    address nodeA;
    address nodeB;

    function setUp() public {
        nodeRegistry = new NodeRegistry();
        rootRegistry = new EpochRootRegistry(nodeRegistry);
        nodeA = vm.addr(nodeAKey);
        nodeB = vm.addr(nodeBKey);
    }

    function _registerStructHash(address node) internal pure returns (bytes32) {
        return keccak256(abi.encode(keccak256("Register(address node)"), node));
    }

    function _domainSeparator(string memory name, address verifyingContract) internal view returns (bytes32) {
        return keccak256(
            abi.encode(
                keccak256("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"),
                keccak256(bytes(name)),
                keccak256(bytes("1")),
                block.chainid,
                verifyingContract
            )
        );
    }

    function _registerNode(uint256 key, address node) internal {
        bytes32 digest = keccak256(
            abi.encodePacked("\x19\x01", _domainSeparator("Symposia.NodeRegistry", address(nodeRegistry)), _registerStructHash(node))
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(key, digest);
        nodeRegistry.register(node, abi.encodePacked(r, s, v));
    }

    function _signRoot(uint256 privateKey, address node, uint64 epoch, bytes32 root) internal view returns (bytes memory) {
        bytes32 structHash =
            keccak256(abi.encode(keccak256("SubmitRoot(address node,uint64 epoch,bytes32 root)"), node, epoch, root));
        bytes32 digest = keccak256(
            abi.encodePacked("\x19\x01", _domainSeparator("Symposia.EpochRootRegistry", address(rootRegistry)), structHash)
        );
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(privateKey, digest);
        return abi.encodePacked(r, s, v);
    }

    // TC-3.1 / Gherkin: registered node submits an epoch root.
    function test_RegisteredNodeSubmitsRoot_Succeeds() public {
        _registerNode(nodeAKey, nodeA);
        bytes32 root = keccak256("epoch-0-manifest");
        rootRegistry.submitRoot(nodeA, 0, root, _signRoot(nodeAKey, nodeA, 0, root));

        (uint64 epoch, bytes32 got) = rootRegistry.getLatestRoot(nodeA);
        assertEq(epoch, 0);
        assertEq(got, root);
    }

    // TC-3.2: consecutive epochs recorded distinctly, latest wins for getLatestRoot.
    function test_ConsecutiveEpochs_TrackLatest() public {
        _registerNode(nodeAKey, nodeA);
        bytes32 root0 = keccak256("root-0");
        bytes32 root1 = keccak256("root-1");
        rootRegistry.submitRoot(nodeA, 0, root0, _signRoot(nodeAKey, nodeA, 0, root0));
        rootRegistry.submitRoot(nodeA, 1, root1, _signRoot(nodeAKey, nodeA, 1, root1));

        (uint64 epoch, bytes32 got) = rootRegistry.getLatestRoot(nodeA);
        assertEq(epoch, 1);
        assertEq(got, root1);
        assertEq(rootRegistry.getRoot(nodeA, 0), root0);
    }

    // TC-3.3: two nodes submitting for the same epoch don't collide.
    function test_TwoNodesSameEpoch_NoCollision() public {
        _registerNode(nodeAKey, nodeA);
        _registerNode(nodeBKey, nodeB);
        bytes32 rootA = keccak256("root-a");
        bytes32 rootB = keccak256("root-b");
        rootRegistry.submitRoot(nodeA, 5, rootA, _signRoot(nodeAKey, nodeA, 5, rootA));
        rootRegistry.submitRoot(nodeB, 5, rootB, _signRoot(nodeBKey, nodeB, 5, rootB));

        assertEq(rootRegistry.getRoot(nodeA, 5), rootA);
        assertEq(rootRegistry.getRoot(nodeB, 5), rootB);
    }

    // TC-3.4 / TC-7.2 (FR10, AC7): identical resubmission is a safe no-op.
    function test_IdenticalResubmission_IsIdempotentNoOp() public {
        _registerNode(nodeAKey, nodeA);
        bytes32 root = keccak256("root-0");
        bytes memory sig = _signRoot(nodeAKey, nodeA, 0, root);
        rootRegistry.submitRoot(nodeA, 0, root, sig);
        rootRegistry.submitRoot(nodeA, 0, root, sig);

        (uint64 epoch, bytes32 got) = rootRegistry.getLatestRoot(nodeA);
        assertEq(epoch, 0);
        assertEq(got, root);
    }

    // TC-3.5 (Arch's resolution of the QA open question): a conflicting
    // resubmission for an already-submitted epoch is rejected outright.
    function test_ConflictingResubmission_Reverts() public {
        _registerNode(nodeAKey, nodeA);
        bytes32 root = keccak256("root-0");
        bytes32 otherRoot = keccak256("different-root");
        rootRegistry.submitRoot(nodeA, 0, root, _signRoot(nodeAKey, nodeA, 0, root));

        vm.expectRevert("EpochRootRegistry: conflicting resubmission for epoch");
        rootRegistry.submitRoot(nodeA, 0, otherRoot, _signRoot(nodeAKey, nodeA, 0, otherRoot));
    }

    // TC-4.1 / Gherkin: unregistered node cannot submit a root.
    function test_UnregisteredNode_SubmissionReverts() public {
        bytes32 root = keccak256("root-0");
        vm.expectRevert("EpochRootRegistry: node not registered");
        rootRegistry.submitRoot(nodeA, 0, root, _signRoot(nodeAKey, nodeA, 0, root));
    }

    // TC-4.2: no root persisted after a rejected unregistered submission.
    function test_RejectedSubmission_LeavesNoRoot() public {
        bytes32 root = keccak256("root-0");
        vm.expectRevert("EpochRootRegistry: node not registered");
        rootRegistry.submitRoot(nodeA, 0, root, _signRoot(nodeAKey, nodeA, 0, root));

        assertEq(rootRegistry.getRoot(nodeA, 0), bytes32(0));
    }

    // TC-5.1 / Gherkin: forged submission signed by a different key reverts.
    function test_ForgedSignature_WrongSigner_Reverts() public {
        _registerNode(nodeAKey, nodeA);
        bytes32 root = keccak256("root-0");
        bytes memory wrongSig = _signRoot(nodeBKey, nodeA, 0, root);

        vm.expectRevert("EpochRootRegistry: invalid signature");
        rootRegistry.submitRoot(nodeA, 0, root, wrongSig);
    }

    // TC-5.2 / Gherkin: tampered payload (root altered post-signing) reverts.
    function test_TamperedPayload_Reverts() public {
        _registerNode(nodeAKey, nodeA);
        bytes32 signedRoot = keccak256("root-as-signed");
        bytes32 tamperedRoot = keccak256("root-tampered");
        bytes memory sig = _signRoot(nodeAKey, nodeA, 0, signedRoot);

        vm.expectRevert("EpochRootRegistry: invalid signature");
        rootRegistry.submitRoot(nodeA, 0, tamperedRoot, sig);
    }

    // TC-6.1 / TC-6.6: exact round-trip of the submitted root.
    function test_ReadRoot_RoundTripsExactly() public {
        _registerNode(nodeAKey, nodeA);
        bytes32 root = keccak256("exact-root-value");
        rootRegistry.submitRoot(nodeA, 3, root, _signRoot(nodeAKey, nodeA, 3, root));
        assertEq(rootRegistry.getRoot(nodeA, 3), root);
    }

    // TC-6.4: registered node with no submissions yet has a well-defined,
    // non-error read state (distinguished from "never registered").
    function test_RegisteredNodeNoSubmissions_LatestReadReverts() public {
        _registerNode(nodeAKey, nodeA);
        vm.expectRevert("EpochRootRegistry: no submissions for node");
        rootRegistry.getLatestRoot(nodeA);
        assertEq(rootRegistry.getRoot(nodeA, 0), bytes32(0));
    }

    // TC-6.7: no cross-node data leakage.
    function test_NoCrossNodeLeakage() public {
        _registerNode(nodeAKey, nodeA);
        _registerNode(nodeBKey, nodeB);
        bytes32 rootA = keccak256("root-a");
        rootRegistry.submitRoot(nodeA, 1, rootA, _signRoot(nodeAKey, nodeA, 1, rootA));

        assertEq(rootRegistry.getRoot(nodeB, 1), bytes32(0));
    }
}
