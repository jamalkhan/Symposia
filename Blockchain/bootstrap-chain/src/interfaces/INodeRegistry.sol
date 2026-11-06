// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title INodeRegistry
/// @notice A Node (one address / node key) runs 0, 1 or many Node Services
/// (#159). Each Node Service is registered, staked, scored and penalized on
/// its own. Node-level views (`statusOf`, `stakeOf`, `typeOf`) aggregate over
/// a node's services for callers that predate #159.
///
/// Narrow external interface the RewardDistributor and
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

    /// @notice The Node Service type. Kept as `NodeType` for ABI and
    /// storage-layout compatibility; ordinals are on-chain config keys, so
    /// values are only ever appended.
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
        Compute,
        // #159/#160: Node Services added with the Node Services model.
        BlobGateway,
        BlockchainGateway,
        TelemetryCommittee
    }

    /// @notice Node-level status: Active if any service is Active, otherwise the
    /// most advanced of PendingVerification, Suspended, Deregistering, Banned.
    function statusOf(address node) external view returns (NodeStatus);
    /// @notice The node's primary (first registered) Node Service.
    function typeOf(address node) external view returns (NodeType);
    /// @notice Total stake across all of the node's services.
    function stakeOf(address node) external view returns (uint256);
    /// @notice Last verification epoch of the primary service.
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

    // --- Node Services (#159) ---

    function servicesOf(address node) external view returns (NodeType[] memory);
    function hasPrimaryService(address node) external view returns (bool);
    function serviceStatusOf(address node, NodeType service) external view returns (NodeStatus);
    function serviceStakeOf(address node, NodeType service) external view returns (uint256);
    function serviceLastVerifiedEpochOf(address node, NodeType service) external view returns (uint256);

    /// @notice Slashes one service's stake only (service-specific penalties,
    /// e.g. the storage penalty state machine).
    function applyServiceSlash(address node, NodeType service, uint256 amount, address recipient) external;

    /// @notice Bans one service only; the node's other services keep running.
    function banService(address node, NodeType service, uint256 banExpiry) external;
}
