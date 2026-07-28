// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import {SafeERC20} from "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";
import {RegionDistribution} from "./foundation/RegionDistribution.sol";

/// @notice Narrow slice of `StakingNodeRegistry`'s ABI this contract calls
/// into. `topUp` and `recordVerification` both exist on the real #52
/// contract; declared here rather than importing the concrete contract so
/// this file only depends on the call shapes it actually uses.
interface IStakingRegistryCalls {
    function topUp(address node, uint256 amount) external;
    function recordVerification(address node, bool passed, uint256 epoch) external;
}

/// @notice Narrow slice of `SlashingController`'s ABI this contract calls
/// into for the early-exit path.
interface ISlashingControllerStakeCommitment {
    function triggerStakeCommitmentViolation(address node, bytes32 reason) external;
}

/// @title FoundationRegistry
/// @notice Issue #57 (Phase 0 foundation node infrastructure). A thin
/// companion contract to #52's `StakingNodeRegistry` — not a fork of it —
/// adding the foundation-specific layer (stake-source/5x-minimum
/// enforcement, region-distribution cap, the 12-month operational floor,
/// early-exit handling, and foundation status tagging) on top, per the Arch
/// pass's "additive, not duplicated" guidance.
///
/// Composition note / deviation from a literal reading of the Arch pass's
/// `NodeRegistry.register(...)` call: `StakingNodeRegistry.register` is
/// strictly `msg.sender`-keyed (a node stakes for itself), so this contract
/// cannot call it on a node's behalf without becoming the registry's
/// `msg.sender` of record (which would key the stake to this contract's
/// address, not the node's — breaking every other #52/#53 lookup keyed by
/// node address). Instead: the node completes its own initial
/// `StakingNodeRegistry.register(...)` call as any node would (any
/// non-zero stake), and `registerFoundationNode` here tops that stake up to
/// (at least) the 5x-verifier-minimum bar using tokens pulled directly from
/// the configured Foundation Reserve address via `StakingNodeRegistry.topUp`
/// — a call whose only effect is `transferFrom(msg.sender=<this contract>,
/// registry, amount)` crediting `node`'s stake, which composes cleanly with
/// #52 without any contract-level change to it. The reserve-sourced
/// `transferFrom` and the `foundation_node=true`/floor-date flag write both
/// happen inside this same function call, so there is no window where the
/// flag could be set without the stake bar being met (closing the Arch
/// pass's TC-2.4 concern).
contract FoundationRegistry is GovernedUpgradeable {
    using SafeERC20 for IERC20;

    struct FoundationRecord {
        bool isFoundation;
        bytes32 region;
        uint256 stake;
        uint256 registeredAt;
        uint256 floorEndDate;
    }

    /// @custom:storage-location erc7201:symposia.FoundationRegistry
    struct FoundationStorage {
        mapping(address => FoundationRecord) records;
        mapping(bytes32 => uint256) regionCounts;
        uint256 totalFoundationNodes;
        bool genesisUsed;
        address[] foundationNodeList;
    }

    bytes32 private constant FOUNDATION_STORAGE_LOCATION = keccak256("symposia.storage.FoundationRegistry");

    /// @notice 12-month operational floor (§5.1). Modeled as a fixed
    /// 365-day duration from registration; satisfied inclusive of the end
    /// date (`block.timestamp >= floorEndDate` counts as on-time, not
    /// early) — the Arch pass's explicit resolution of the exact-12-month
    /// boundary open question.
    uint256 internal constant FLOOR_DURATION = 365 days;

    event FoundationNodeRegistered(address indexed node, bytes32 region, uint256 stake, uint256 floorEndDate);
    event EarlyExitEvent(address indexed node, bytes32 justificationRef, uint256 floorEndDate, uint256 exitedAt);
    event FoundationNodeDeregistered(address indexed node, uint256 exitedAt);
    event GenesisAttestation(address indexed node, uint256 epoch);
    event QuorumAttestation(address indexed node, uint256 epoch, uint256 attestorCount);

    error NotFoundationOps(address caller);
    error AlreadyFoundationNode(address node);
    error NotFoundationNode(address node);
    error InsufficientFoundationStake(uint256 stakeAfter, uint256 required);
    error InvalidNodeStatus(address node);
    error GenesisAlreadyUsed();
    error InsufficientQuorum(uint256 provided, uint256 required);
    error AttestorNotFoundationVerifier(address attestor);

    function _foundationStorage() private pure returns (FoundationStorage storage $) {
        bytes32 slot = FOUNDATION_STORAGE_LOCATION;
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

    /// @notice Foundation-ops multisig gate (config-supplied address, same
    /// mechanism `GovernedUpgradeable` already uses for the Timelock, but a
    /// distinct role) for routine registration/deregistration/quorum-attest
    /// actions, so every foundation-node registration does not have to go
    /// through the full DAO governance Timelock. Genesis attestation (a
    /// one-time bootstrap-only action) is gated by the Timelock itself
    /// instead — see `genesisAttestFoundationNode`.
    modifier onlyFoundationOps() {
        address ops = config().getAddress(ConfigKeys.FOUNDATION_OPS_ROLE);
        if (msg.sender != ops) revert NotFoundationOps(msg.sender);
        _;
    }

    function _registryAddress() internal view returns (address) {
        return config().getAddress(ConfigKeys.REGISTRY_ADDRESS);
    }

    function _nodeRegistry() internal view returns (INodeRegistry) {
        return INodeRegistry(_registryAddress());
    }

    // --- Registration (AC2, AC4) ---

    /// @notice Registers `node` as a foundation node. `sig` is reserved for
    /// a future off-chain attestation proof (e.g. the foundation-ops
    /// relayer's signed authorization payload); it is accepted for ABI
    /// stability with the Arch pass's specified signature but not verified
    /// on-chain in this MVP pass — the `onlyFoundationOps` gate is the
    /// actual authorization boundary today.
    function registerFoundationNode(address node, string calldata region, uint256 stake, bytes calldata sig)
        external
        onlyFoundationOps
        whenNotPaused
    {
        sig; // see NatSpec above
        FoundationStorage storage $ = _foundationStorage();
        if ($.records[node].isFoundation) revert AlreadyFoundationNode(node);

        INodeRegistry.NodeStatus status = _nodeRegistry().statusOf(node);
        if (status == INodeRegistry.NodeStatus.Unregistered || status == INodeRegistry.NodeStatus.Banned) {
            revert InvalidNodeStatus(node);
        }

        // Reserve-sourced stake top-up (see contract-level NatSpec on why
        // this is `topUp`, not `register`).
        address reserve = config().getAddress(ConfigKeys.FOUNDATION_RESERVE_ADDRESS);
        IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
        address registryAddr = _registryAddress();
        token.safeTransferFrom(reserve, address(this), stake);
        token.forceApprove(registryAddr, stake);
        IStakingRegistryCalls(registryAddr).topUp(node, stake);

        uint256 minVerifierStake = _nodeRegistry().minStakeFor(INodeRegistry.NodeType.Verifier, 0);
        uint256 required = minVerifierStake * 5;
        uint256 stakeAfter = _nodeRegistry().stakeOf(node);
        if (stakeAfter < required) revert InsufficientFoundationStake(stakeAfter, required);

        bytes32 regionKey = keccak256(bytes(region));
        uint256 capBps = config().getUint(ConfigKeys.REGION_DISTRIBUTION_CAP_BPS);
        RegionDistribution.checkCapOnAdd(regionKey, $.regionCounts[regionKey], $.totalFoundationNodes, capBps);

        uint256 floorEndDate = block.timestamp + FLOOR_DURATION;
        $.records[node] = FoundationRecord({
            isFoundation: true,
            region: regionKey,
            stake: stakeAfter,
            registeredAt: block.timestamp,
            floorEndDate: floorEndDate
        });
        $.regionCounts[regionKey] += 1;
        $.totalFoundationNodes += 1;
        $.foundationNodeList.push(node);

        emit FoundationNodeRegistered(node, regionKey, stakeAfter, floorEndDate);
    }

    // --- Deregistration / early exit (AC5) ---

    /// @notice Deregisters a foundation node. If called before the node's
    /// 12-month floor has elapsed (`block.timestamp < floorEndDate` —
    /// exactly `floorEndDate` itself counts as on-time, not early), this is
    /// treated as an early exit: a distinct `EarlyExitEvent` is emitted and
    /// `SlashingController.triggerStakeCommitmentViolation` is invoked
    /// instead of a silent standard deregistration. On or after the floor,
    /// this is a standard deregistration with no early-exit event or
    /// slashing evaluation.
    function deregisterFoundationNode(address node, bytes32 justificationRef) external onlyFoundationOps whenNotPaused {
        FoundationStorage storage $ = _foundationStorage();
        FoundationRecord storage rec = $.records[node];
        if (!rec.isFoundation) revert NotFoundationNode(node);

        bool early = block.timestamp < rec.floorEndDate;
        bytes32 region = rec.region;
        uint256 floorEndDate = rec.floorEndDate;

        rec.isFoundation = false;
        $.regionCounts[region] -= 1;
        $.totalFoundationNodes -= 1;

        if (early) {
            emit EarlyExitEvent(node, justificationRef, floorEndDate, block.timestamp);
            address slashingController = config().getAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER);
            ISlashingControllerStakeCommitment(slashingController).triggerStakeCommitmentViolation(node, justificationRef);
        } else {
            emit FoundationNodeDeregistered(node, block.timestamp);
        }
    }

    // --- Region-verification quorum (AC3; foundation-only bootstrap window) ---

    /// @notice One-time, governance-Timelock-gated genesis path satisfying
    /// region-verification quorum for the very first foundation node,
    /// before any peer foundation node exists to attest it — the Arch
    /// pass's answer to "manually attested seed configuration" (§1.3).
    /// Explicitly bounded (usable exactly once for the contract's lifetime,
    /// and only for an already-registered foundation node), not an
    /// unbounded admin backdoor.
    function genesisAttestFoundationNode(address node, uint256 epoch) external onlyTimelock {
        FoundationStorage storage $ = _foundationStorage();
        if ($.genesisUsed) revert GenesisAlreadyUsed();
        if (!$.records[node].isFoundation) revert NotFoundationNode(node);

        $.genesisUsed = true;
        IStakingRegistryCalls(_registryAddress()).recordVerification(node, true, epoch);
        emit GenesisAttestation(node, epoch);
    }

    /// @notice Quorum-3 region-verification attestation using only
    /// foundation-node attestors. `verifier-nodes.md`'s same-operator/ASN
    /// exclusion is explicitly waived here — and ONLY here — because all
    /// attesting parties are required to be foundation nodes (which by
    /// definition share one real-world operator during Phase 0); this is a
    /// distinct code path from any general verifier-admission logic so the
    /// waiver cannot leak once community verifiers exist.
    ///
    /// Requires this contract to hold the registry's configured
    /// `REGISTRY_VERIFIER_ROLE` so it may call `recordVerification` on the
    /// underlying `StakingNodeRegistry` — a deployment/config wiring
    /// requirement, not a code dependency.
    function attestRegionVerification(address node, address[] calldata attestors, uint256 epoch)
        external
        onlyFoundationOps
    {
        FoundationStorage storage $ = _foundationStorage();
        if (!$.records[node].isFoundation) revert NotFoundationNode(node);
        if (attestors.length < 3) revert InsufficientQuorum(attestors.length, 3);
        for (uint256 i = 0; i < attestors.length; i++) {
            if (!$.records[attestors[i]].isFoundation) revert AttestorNotFoundationVerifier(attestors[i]);
        }

        IStakingRegistryCalls(_registryAddress()).recordVerification(node, true, epoch);
        emit QuorumAttestation(node, epoch, attestors.length);
    }

    // --- Reward-surplus routing hook (AC6; called by RewardDistributor) ---

    /// @notice Read-only split computation consumed by `RewardDistributor`'s
    /// payout points (issue #53). Foundation nodes earning more than their
    /// configured `operationalCostBaseline` for the epoch have the surplus
    /// routed to the Ecosystem Reserve; non-foundation nodes and foundation
    /// nodes at or below baseline pass through unchanged (`reserveAmount ==
    /// 0`), so community-node payouts are provably unaffected by this hook.
    function routeFoundationPayout(address node, uint256, /*epoch*/ uint256 grossAmount)
        external
        view
        returns (uint256 operatorAmount, uint256 reserveAmount, address reserveRecipient)
    {
        if (!_foundationStorage().records[node].isFoundation) {
            return (grossAmount, 0, address(0));
        }

        uint256 baseline = config().getUint(ConfigKeys.operationalCostBaseline(node));
        if (grossAmount <= baseline) {
            return (grossAmount, 0, address(0));
        }

        uint256 surplus = grossAmount - baseline;
        return (baseline, surplus, config().getAddress(ConfigKeys.ECOSYSTEM_RESERVE_ADDRESS));
    }

    // --- Views (AC2, AC4, AC7) ---

    function isFoundationNode(address node) external view returns (bool) {
        return _foundationStorage().records[node].isFoundation;
    }

    function getFoundationRecord(address node) external view returns (FoundationRecord memory) {
        return _foundationStorage().records[node];
    }

    /// @notice Total currently-active foundation nodes — the read surface a
    /// public node-directory indexer (§7, out-of-scope on-chain service; a
    /// TODO/stub per the Arch pass's "event-sourced off-chain projection,
    /// not a new contract" guidance) or region-cap consumers would use.
    function foundationNodeCount() external view returns (uint256) {
        return _foundationStorage().totalFoundationNodes;
    }

    /// @notice Enumerates ALL foundation nodes ever registered (including
    /// since-deregistered ones — check `isFoundationNode` for current
    /// status). Convenience for small (single-digit-to-low-tens per spec)
    /// foundation-node counts; not intended to scale to the general
    /// verifier pool.
    function foundationNodeAt(uint256 index) external view returns (address) {
        return _foundationStorage().foundationNodeList[index];
    }

    function foundationNodeListLength() external view returns (uint256) {
        return _foundationStorage().foundationNodeList.length;
    }

    function regionCount(bytes32 regionKey) external view returns (uint256) {
        return _foundationStorage().regionCounts[regionKey];
    }
}
