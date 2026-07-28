// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";
import {ComputeTierRegistry} from "./ComputeTierRegistry.sol";

/// @title ComputeNodeManifest
/// @notice Issue #90: the compute-specific declaration this node-type layers
/// on top of the two already-Arch'd primitives it composes -- `StakingNodeRegistry`
/// (#74, via the narrow `INodeRegistry` interface) and `ComputeTierRegistry`
/// (#89). This contract owns none of the identity or economic state those two
/// already own; it records only what neither carries: declared Postgres
/// major version(s), declared extensions, per-node capacity limits (max
/// databases / vCPU / RAM), HIPAA-eligibility opt-in, and the tier the node
/// held at declaration time.
///
/// `declareManifest` re-checks its own preconditions (registered + staked,
/// benchmark at or above Tier 3) rather than trusting an off-chain wizard to
/// have gated the flow correctly -- these are trust boundaries per the
/// issue's Gherkin scenarios ("cannot stake without passing benchmark",
/// "insufficient stake blocks registration"), not just UX affordances.
/// Sufficiency-of-stake itself is already enforced by `StakingNodeRegistry.register`
/// (it reverts on an underfunded `register` call), so this contract only
/// needs to confirm the node reached a registered status, not re-derive the
/// stake formula.
///
/// The 80%/85% vCPU/RAM over-subscription guardrail (compute-nodes.md) is
/// deliberately NOT re-derived or enforced here: it is advisory ("warns, does
/// not silently reject" per the spec), computed off-chain against the
/// operator's own physical hardware, which this contract has no independent
/// view of. `overSubscriptionAcknowledged` is stored purely as an audit flag
/// for #91's retrospective over-subscription detection.
contract ComputeNodeManifest is GovernedUpgradeable {
    struct Manifest {
        uint8[] pgMajorVersions;
        uint32 maxDatabases;
        uint32 maxVcpu;
        uint64 maxRamMB;
        ComputeTierRegistry.ComputeTier tier;
        bool hipaaEligible;
        bool overSubscriptionAcknowledged;
        uint64 declaredAt;
    }

    /// @custom:storage-location erc7201:symposia.ComputeNodeManifest
    struct ManifestStorage {
        mapping(address => Manifest) manifests;
        mapping(address => string[]) extensionsOf;
        mapping(address => bool) declared;
    }

    bytes32 private constant MANIFEST_STORAGE_LOCATION = keccak256("symposia.storage.ComputeNodeManifest");

    event ComputeNodeDeclared(
        address indexed node,
        ComputeTierRegistry.ComputeTier tier,
        uint32 maxDatabases,
        uint32 maxVcpu,
        uint64 maxRamMB,
        bool hipaaEligible,
        bool overSubscriptionAcknowledged,
        uint64 declaredAt
    );
    event ExtensionsDeclared(address indexed node, string[] extensions);
    event PgVersionsDeclared(address indexed node, uint8[] pgMajorVersions);

    error NodeNotRegistered(address node);
    error BenchmarkBelowTier3(address node);
    error NoPgVersionDeclared(address node);
    error InvalidCapacityLimits(address node);

    function _storage() private pure returns (ManifestStorage storage $) {
        bytes32 slot = MANIFEST_STORAGE_LOCATION;
        assembly {
            $.slot := slot
        }
    }

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        _disableInitializers();
    }

    function initialize(IProtocolConfig initialConfig) external initializer {
        __GovernedUpgradeable_init(initialConfig);
    }

    function _stakingRegistry() private view returns (INodeRegistry) {
        return INodeRegistry(config().getAddress(ConfigKeys.REGISTRY_ADDRESS));
    }

    function _tierRegistry() private view returns (ComputeTierRegistry) {
        return ComputeTierRegistry(config().getAddress(ConfigKeys.COMPUTE_TIER_REGISTRY_ADDRESS));
    }

    /// @notice Declares (or re-declares) the calling node's compute manifest.
    /// The three preconditions -- registered/staked, benchmark >= Tier 3,
    /// at least one declared Postgres version and a nonzero database ceiling
    /// -- are enforced here regardless of whatever an off-chain onboarding
    /// wizard already checked, since a scripted caller could otherwise bypass
    /// the guided flow entirely.
    function declareManifest(
        uint8[] calldata pgMajorVersions,
        string[] calldata extensions,
        uint32 maxDatabases,
        uint32 maxVcpu,
        uint64 maxRamMB,
        bool hipaaEligible,
        bool overSubscriptionAcknowledged
    ) external whenNotPaused {
        address node = msg.sender;

        INodeRegistry.NodeStatus status = _stakingRegistry().statusOf(node);
        if (status == INodeRegistry.NodeStatus.Unregistered || status == INodeRegistry.NodeStatus.Banned) {
            revert NodeNotRegistered(node);
        }

        ComputeTierRegistry.ComputeTier tier = _tierRegistry().currentTierOf(node);
        if (tier == ComputeTierRegistry.ComputeTier.Rejected) {
            revert BenchmarkBelowTier3(node);
        }

        if (pgMajorVersions.length == 0) revert NoPgVersionDeclared(node);
        if (maxDatabases == 0) revert InvalidCapacityLimits(node);

        ManifestStorage storage $ = _storage();
        Manifest storage m = $.manifests[node];
        m.pgMajorVersions = pgMajorVersions;
        m.maxDatabases = maxDatabases;
        m.maxVcpu = maxVcpu;
        m.maxRamMB = maxRamMB;
        m.tier = tier;
        m.hipaaEligible = hipaaEligible;
        m.overSubscriptionAcknowledged = overSubscriptionAcknowledged;
        m.declaredAt = uint64(block.timestamp);

        $.extensionsOf[node] = extensions;
        $.declared[node] = true;

        emit PgVersionsDeclared(node, pgMajorVersions);
        emit ExtensionsDeclared(node, extensions);
        emit ComputeNodeDeclared(
            node, tier, maxDatabases, maxVcpu, maxRamMB, hipaaEligible, overSubscriptionAcknowledged, m.declaredAt
        );
    }

    // --- Views ---

    function manifestOf(address node) external view returns (Manifest memory) {
        return _storage().manifests[node];
    }

    function extensionsOf(address node) external view returns (string[] memory) {
        return _storage().extensionsOf[node];
    }

    function isHipaaEligible(address node) external view returns (bool) {
        return _storage().manifests[node].hipaaEligible;
    }

    function isDeclared(address node) external view returns (bool) {
        return _storage().declared[node];
    }
}
