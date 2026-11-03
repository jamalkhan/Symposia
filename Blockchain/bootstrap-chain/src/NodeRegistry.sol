// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {EIP712} from "@openzeppelin/contracts/utils/cryptography/EIP712.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";

/// @title NodeRegistry
/// @notice Phase 0 minimal bootstrap chain contract (issue #110). Records node
/// public-key identities so Blob Storage nodes can complete cold-start step 1
/// ("the node generates its keypair and registers on-chain") per
/// Requirements/BlobStorage/metadata-architecture.md. This is the protocol's
/// own L3 genesis contract set, not a throwaway environment migrated later —
/// see the Arch pass on issue #110 for why that continuity guarantee (FR9)
/// requires deploying on the real chain from day one rather than a
/// bootstrap-only substitute.
///
/// Issue #129 extends each record with a node-signed endpoint (address hint)
/// and makes membership enumerable, so nodes can discover and dial each other.
///
/// Non-upgradeable by design: this becomes an authoritative identity record
/// consumed by dispute resolution, so no admin key should be able to alter it.
contract NodeRegistry is EIP712 {
    bytes32 private constant REGISTER_TYPEHASH =
        keccak256("Register(address node,string endpoint,uint256 nonce)");

    uint256 public constant MAX_ENDPOINT_LENGTH = 256;

    mapping(address => bool) private _registered;
    mapping(address => string) private _endpoint;
    mapping(address => uint256) private _nonce;
    address[] private _nodes;

    event NodeRegistered(address indexed node, string endpoint);
    event NodeEndpointUpdated(address indexed node, string endpoint);

    constructor() EIP712("Symposia.NodeRegistry", "1") {}

    /// @notice Registers `node` with its advertised `endpoint`, or updates the
    /// endpoint of an already-registered node, authenticated by an EIP-712
    /// signature over (node, endpoint, nonce) produced by the node's own
    /// keypair (issues #109, #129). A relayer (the Bootstrap Chain Gateway)
    /// may submit this transaction on the node's behalf and pay gas, since
    /// the node's identity is proven by the signature, not by `msg.sender`.
    /// Idempotent: a repeat call for an already-registered node with an
    /// unchanged endpoint is a safe no-op, not a revert (#110 FR10).
    /// `nonce` must equal `nonceOf(node)`, so an old signature cannot be
    /// replayed to roll an endpoint back (#129 FR-B3).
    function register(address node, string calldata endpoint, uint256 nonce, bytes calldata signature) external {
        uint256 length = bytes(endpoint).length;
        require(length > 0 && length <= MAX_ENDPOINT_LENGTH, "NodeRegistry: invalid endpoint");

        bool alreadyRegistered = _registered[node];
        if (alreadyRegistered && keccak256(bytes(_endpoint[node])) == keccak256(bytes(endpoint))) {
            return;
        }

        require(nonce == _nonce[node], "NodeRegistry: invalid nonce");

        bytes32 structHash = keccak256(abi.encode(REGISTER_TYPEHASH, node, keccak256(bytes(endpoint)), nonce));
        address signer = ECDSA.recover(_hashTypedDataV4(structHash), signature);
        require(signer == node, "NodeRegistry: invalid signature");

        _nonce[node] = nonce + 1;
        _endpoint[node] = endpoint;

        if (alreadyRegistered) {
            emit NodeEndpointUpdated(node, endpoint);
        } else {
            _registered[node] = true;
            _nodes.push(node);
            emit NodeRegistered(node, endpoint);
        }
    }

    /// @notice Returns whether `node` has completed registration.
    function isRegistered(address node) external view returns (bool) {
        return _registered[node];
    }

    /// @notice The endpoint `node` last advertised; empty if unregistered.
    function endpointOf(address node) external view returns (string memory) {
        return _endpoint[node];
    }

    /// @notice The nonce the next signed registration for `node` must carry.
    function nonceOf(address node) external view returns (uint256) {
        return _nonce[node];
    }

    /// @notice Number of registered nodes, for enumeration via `nodeAt`.
    function nodeCount() external view returns (uint256) {
        return _nodes.length;
    }

    /// @notice The registered node at `index` (registration order).
    function nodeAt(uint256 index) external view returns (address) {
        return _nodes[index];
    }
}
