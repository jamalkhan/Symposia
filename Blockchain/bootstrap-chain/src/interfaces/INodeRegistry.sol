// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title INodeRegistry
/// @notice Narrow external interface the RewardDistributor and
/// SlashingController contracts (issue #52) call into, kept separate from
/// StakingNodeRegistry's full ABI so each contract's internal implementation
/// can evolve independently (per the Arch pass's cross-contract coupling
/// guidance).
interface INodeRegistry {
    enum NodeStatus {
        Unregistered,
        PendingVerification,
        Active,
        Suspended,
        Banned,
        Deregistering
    }

    enum NodeType {
        Storage,
        OLTP,
        Analytics,
        Consensus,
        Serverless,
        Verifier,
        EmailIP,
        // Appended rather than inserted to avoid renumbering existing
        // ordinals used as on-chain config keys (issue #90 decision:
        // distinct from OLTP -- compute-nodes.md's continuous per-vCPU
        // stake formula and fee-funded reward model diverge from OLTP's
        // tiered-step formula and emission-funded model).
        Compute
    }

    function statusOf(address node) external view returns (NodeStatus);
    function typeOf(address node) external view returns (NodeType);
    function stakeOf(address node) external view returns (uint256);
    function lastVerifiedEpochOf(address node) external view returns (uint256);
    function minStakeFor(NodeType nodeType, uint256 capacity) external view returns (uint256);

    /// @notice Called by SlashingController to reduce a node's recorded
    /// stake balance and, since the registry itself custodies staked
    /// tokens, to move the slashed `amount` out to `recipient` (a burn
    /// address or governance-configured redistribution target — the
    /// disposition choice itself is decided by SlashingController from
    /// config, per FR-5). Pass `address(0)` for `recipient` to skip the
    /// token transfer (bookkeeping-only slash).
    function applySlash(address node, uint256 amount, address recipient) external;

    /// @notice Called by SlashingController on Stage 4 confirmation or a
    /// non-hardware violation ban — removes the node from the active set
    /// and marks the identity Banned so it must re-register under a fresh
    /// entry to rejoin (FR-2).
    function banNode(address node, uint256 banExpiry) external;
}
