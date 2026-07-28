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
/// Non-upgradeable by design: this becomes an authoritative identity record
/// consumed by dispute resolution, so no admin key should be able to alter it.
contract NodeRegistry is EIP712 {
    bytes32 private constant REGISTER_TYPEHASH = keccak256("Register(address node)");

    mapping(address => bool) private _registered;

    event NodeRegistered(address indexed node);

    constructor() EIP712("Symposia.NodeRegistry", "1") {}

    /// @notice Registers `node` as an on-chain identity, authenticated by an
    /// EIP-712 signature produced by the node's own keypair (per issue #109).
    /// A relayer (the Bootstrap Chain Gateway) may submit this transaction on
    /// the node's behalf and pay gas, since the node's identity is proven by
    /// the signature, not by `msg.sender` (Functional Requirement 8).
    /// Idempotent: a repeat call for an already-registered node is a safe
    /// no-op, not a revert (Functional Requirement 10).
    function register(address node, bytes calldata signature) external {
        if (_registered[node]) {
            return;
        }

        bytes32 structHash = keccak256(abi.encode(REGISTER_TYPEHASH, node));
        bytes32 digest = _hashTypedDataV4(structHash);
        address signer = ECDSA.recover(digest, signature);
        require(signer == node, "NodeRegistry: invalid signature");

        _registered[node] = true;
        emit NodeRegistered(node);
    }

    /// @notice Returns whether `node` has completed registration.
    function isRegistered(address node) external view returns (bool) {
        return _registered[node];
    }
}
