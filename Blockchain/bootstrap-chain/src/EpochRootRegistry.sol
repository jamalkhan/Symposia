// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {EIP712} from "@openzeppelin/contracts/utils/cryptography/EIP712.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {NodeRegistry} from "./NodeRegistry.sol";

/// @title EpochRootRegistry
/// @notice Phase 0 minimal bootstrap chain contract (issue #110). Accepts
/// signed epoch Merkle roots from registered Blob Storage nodes and serves as
/// the authoritative source for dispute resolution, proof-of-storage
/// verification, and recovery per
/// Requirements/BlobStorage/metadata-architecture.md's Layer 3 description.
contract EpochRootRegistry is EIP712 {
    bytes32 private constant SUBMIT_ROOT_TYPEHASH =
        keccak256("SubmitRoot(address node,uint64 epoch,bytes32 root)");

    NodeRegistry public immutable nodeRegistry;

    // node => epoch => root. A root of bytes32(0) is used as the "no
    // submission for this epoch" sentinel, since a real Merkle root is
    // exceedingly unlikely to hash to zero.
    mapping(address => mapping(uint64 => bytes32)) private _roots;
    mapping(address => uint64) private _latestEpoch;
    mapping(address => bool) private _hasSubmitted;

    event RootSubmitted(address indexed node, uint64 indexed epoch, bytes32 root);

    constructor(NodeRegistry _nodeRegistry) EIP712("Symposia.EpochRootRegistry", "1") {
        nodeRegistry = _nodeRegistry;
    }

    /// @notice Submits a signed epoch Merkle root on behalf of `node`.
    /// Rejects submissions from unregistered nodes (Functional Requirement 4)
    /// and submissions whose signature doesn't verify against `node`
    /// (Functional Requirement 5). A resubmission of the exact same root for
    /// an already-submitted epoch is a safe no-op (Functional Requirement
    /// 10); a resubmission of a *different* root for an already-submitted
    /// epoch is rejected outright, since roots are authoritative for dispute
    /// resolution and reward calculation and must not be silently
    /// overwritten (per the Arch pass on issue #110).
    function submitRoot(address node, uint64 epoch, bytes32 root, bytes calldata signature) external {
        require(nodeRegistry.isRegistered(node), "EpochRootRegistry: node not registered");

        bytes32 existing = _roots[node][epoch];
        if (existing != bytes32(0)) {
            require(existing == root, "EpochRootRegistry: conflicting resubmission for epoch");
            return;
        }

        bytes32 structHash = keccak256(abi.encode(SUBMIT_ROOT_TYPEHASH, node, epoch, root));
        bytes32 digest = _hashTypedDataV4(structHash);
        address signer = ECDSA.recover(digest, signature);
        require(signer == node, "EpochRootRegistry: invalid signature");

        bool hadPriorSubmission = _hasSubmitted[node];
        _roots[node][epoch] = root;
        _hasSubmitted[node] = true;
        if (!hadPriorSubmission || epoch >= _latestEpoch[node]) {
            _latestEpoch[node] = epoch;
        }

        emit RootSubmitted(node, epoch, root);
    }

    /// @notice Returns the most recently submitted epoch and root for `node`.
    /// Reverts if `node` has never submitted a root, so callers can
    /// distinguish "no submissions yet" from a real all-zero root.
    function getLatestRoot(address node) external view returns (uint64 epoch, bytes32 root) {
        require(_hasSubmitted[node], "EpochRootRegistry: no submissions for node");
        epoch = _latestEpoch[node];
        root = _roots[node][epoch];
    }

    /// @notice Returns the root recorded for `node` at `epoch`, or
    /// bytes32(0) if none was submitted.
    function getRoot(address node, uint64 epoch) external view returns (bytes32) {
        return _roots[node][epoch];
    }
}
