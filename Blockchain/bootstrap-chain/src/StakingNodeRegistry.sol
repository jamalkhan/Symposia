// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import {SafeERC20} from "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";
import {ISlashingController} from "./interfaces/ISlashingController.sol";

/// @title StakingNodeRegistry
/// @notice Implements FR-2 of issue #52: staked node registration, status
/// tracking, top-up, voluntary unstake with cooldown, ban-on-Stage-4 /
/// verification-fraud, and the overcommitment invariant check.
///
/// This is deliberately a separate contract from the pre-existing
/// `NodeRegistry` (issue #110), which only records the EIP-712-signed
/// keypair-identity registration needed to complete cold-start step 1 and is
/// intentionally non-upgradeable so it can serve as an authoritative
/// identity record for dispute resolution. That contract's `register`
/// signature and semantics are untouched by this issue. `StakingNodeRegistry`
/// is the FR-2 economic registry — node type, capacity, region claim, and
/// staked collateral — layered on top for the same node addresses; it is
/// intentionally address-keyed rather than requiring a prior call into
/// `NodeRegistry`, so the two can be wired together (e.g. gating staking
/// registration on identity registration) at the integration/deployment
/// layer without a contract-level dependency baked in here.
contract StakingNodeRegistry is GovernedUpgradeable, INodeRegistry {
    using SafeERC20 for IERC20;

    struct NodeRecord {
        NodeType nodeType;
        uint256 capacity;
        bytes32 region;
        uint256 stake;
        NodeStatus status;
        uint256 lastVerifiedEpoch;
        uint256 unstakeCooldownEnd;
        uint256 pendingUnstakeAmount;
        uint256 banExpiry;
    }

    /// @custom:storage-location erc7201:symposia.StakingNodeRegistry
    struct RegistryStorage {
        mapping(address => NodeRecord) records;
    }

    bytes32 private constant REGISTRY_STORAGE_LOCATION = keccak256("symposia.storage.StakingNodeRegistry");

    event Registered(address indexed node, NodeType nodeType, uint256 capacity, bytes32 region, uint256 stake);
    event StakeChanged(address indexed node, uint256 previousStake, uint256 newStake);
    event StatusChanged(address indexed node, NodeStatus previousStatus, NodeStatus newStatus);
    event Deregistered(address indexed node);
    event UnstakeRequested(address indexed node, uint256 amount, uint256 cooldownEnd);
    event VerificationRecorded(address indexed node, bool passed, uint256 epoch);
    event OvercommitmentDetected(address indexed node, uint256 stake, uint256 required);

    error AlreadyRegistered(address node);
    error StillBanned(address node, uint256 banExpiry);
    error InsufficientStake(uint256 provided, uint256 required);
    error NotRegistered(address node);
    error CooldownNotElapsed(uint256 cooldownEnd);
    error NoPendingUnstake();
    error RemainingBelowMinimum(uint256 remaining, uint256 required);
    error NotVerifierRole(address caller);
    error NotSlashingController(address caller);
    error NoOvercommitment(address node);
    error NotComputePenaltyStateMachine(address caller);

    function _registryStorage() private pure returns (RegistryStorage storage $) {
        bytes32 slot = REGISTRY_STORAGE_LOCATION;
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

    modifier onlyVerifierRole() {
        address role = config().getAddress(ConfigKeys.REGISTRY_VERIFIER_ROLE);
        if (msg.sender != role) revert NotVerifierRole(msg.sender);
        _;
    }

    modifier onlySlashingController() {
        address controller = config().getAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER);
        if (msg.sender != controller) revert NotSlashingController(msg.sender);
        _;
    }

    modifier onlyComputePenaltyStateMachine() {
        address allowed = config().getAddress(ConfigKeys.COMPUTE_PENALTY_STATE_MACHINE_ADDRESS);
        if (msg.sender != allowed) revert NotComputePenaltyStateMachine(msg.sender);
        _;
    }

    // --- Views (INodeRegistry) ---

    function statusOf(address node) public view returns (NodeStatus) {
        return _registryStorage().records[node].status;
    }

    function typeOf(address node) external view returns (NodeType) {
        return _registryStorage().records[node].nodeType;
    }

    function stakeOf(address node) public view returns (uint256) {
        return _registryStorage().records[node].stake;
    }

    function lastVerifiedEpochOf(address node) external view returns (uint256) {
        return _registryStorage().records[node].lastVerifiedEpoch;
    }

    function recordOf(address node) external view returns (NodeRecord memory) {
        return _registryStorage().records[node];
    }

    /// @notice `base + perUnit * capacity`, both read live from config,
    /// keyed by node type (FR-2). Reverts on overflow rather than wrapping.
    function minStakeFor(NodeType nodeType, uint256 capacity) public view returns (uint256) {
        uint256 base = config().getUint(ConfigKeys.registryStakeBase(uint8(nodeType)));
        uint256 perUnit = config().getUint(ConfigKeys.registryStakePerUnit(uint8(nodeType)));
        return base + (perUnit * capacity);
    }

    // --- Registration ---

    function register(NodeType nodeType, uint256 capacity, bytes32 region, uint256 stakeAmount)
        external
        whenNotPaused
    {
        NodeRecord storage rec = _registryStorage().records[msg.sender];

        if (
            rec.status == NodeStatus.Active || rec.status == NodeStatus.PendingVerification
                || rec.status == NodeStatus.Suspended
        ) {
            revert AlreadyRegistered(msg.sender);
        }
        if (rec.status == NodeStatus.Banned && block.timestamp < rec.banExpiry) {
            revert StillBanned(msg.sender, rec.banExpiry);
        }

        uint256 required = minStakeFor(nodeType, capacity);
        if (stakeAmount < required) revert InsufficientStake(stakeAmount, required);

        IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
        token.safeTransferFrom(msg.sender, address(this), stakeAmount);

        // Fresh registration entry — overwrites any prior (expired-ban or
        // fully-deregistered) record for this address, per FR-2's "reject
        // reactivation ... without a fresh registration flow" requirement.
        _registryStorage().records[msg.sender] = NodeRecord({
            nodeType: nodeType,
            capacity: capacity,
            region: region,
            stake: stakeAmount,
            status: NodeStatus.PendingVerification,
            lastVerifiedEpoch: 0,
            unstakeCooldownEnd: 0,
            pendingUnstakeAmount: 0,
            banExpiry: 0
        });

        emit Registered(msg.sender, nodeType, capacity, region, stakeAmount);
        emit StatusChanged(msg.sender, NodeStatus.Unregistered, NodeStatus.PendingVerification);
    }

    /// @notice Any address may top up a registered node's stake (FR-2).
    function topUp(address node, uint256 amount) external whenNotPaused {
        NodeRecord storage rec = _registryStorage().records[node];
        if (rec.status == NodeStatus.Unregistered || rec.status == NodeStatus.Banned) {
            revert NotRegistered(node);
        }

        IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
        token.safeTransferFrom(msg.sender, address(this), amount);

        uint256 previous = rec.stake;
        rec.stake = previous + amount;
        emit StakeChanged(node, previous, rec.stake);
    }

    // --- Unstaking ---

    /// @notice Requests withdrawal of `amount` of the caller's stake.
    /// Partial unstake is only allowed if the remaining stake stays at or
    /// above the current minimum for the node's declared capacity.
    function requestUnstake(uint256 amount) external whenNotPaused {
        NodeRecord storage rec = _registryStorage().records[msg.sender];
        if (rec.status == NodeStatus.Unregistered || rec.status == NodeStatus.Banned) {
            revert NotRegistered(msg.sender);
        }
        if (amount > rec.stake) revert InsufficientStake(rec.stake, amount);

        uint256 remaining = rec.stake - amount;
        if (remaining != 0) {
            uint256 required = minStakeFor(rec.nodeType, rec.capacity);
            if (remaining < required) revert RemainingBelowMinimum(remaining, required);
        }

        uint256 cooldown = config().getUint(ConfigKeys.REGISTRY_UNSTAKE_COOLDOWN);
        rec.pendingUnstakeAmount = amount;
        rec.unstakeCooldownEnd = block.timestamp + cooldown;

        NodeStatus previous = rec.status;
        rec.status = NodeStatus.Deregistering;
        emit UnstakeRequested(msg.sender, amount, rec.unstakeCooldownEnd);
        emit StatusChanged(msg.sender, previous, NodeStatus.Deregistering);
    }

    /// @notice Completes a previously requested unstake once its cooldown
    /// has elapsed, transferring the pending amount back to the caller.
    function withdrawUnstake() external whenNotPaused {
        NodeRecord storage rec = _registryStorage().records[msg.sender];
        if (rec.pendingUnstakeAmount == 0) revert NoPendingUnstake();
        if (block.timestamp < rec.unstakeCooldownEnd) revert CooldownNotElapsed(rec.unstakeCooldownEnd);

        uint256 amount = rec.pendingUnstakeAmount;
        uint256 previousStake = rec.stake;
        rec.stake = previousStake - amount;
        rec.pendingUnstakeAmount = 0;
        rec.unstakeCooldownEnd = 0;

        NodeStatus previousStatus = rec.status;
        if (rec.stake == 0) {
            rec.status = NodeStatus.Unregistered;
        } else {
            rec.status = NodeStatus.Active;
        }

        IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
        token.safeTransfer(msg.sender, amount);

        emit StakeChanged(msg.sender, previousStake, rec.stake);
        emit StatusChanged(msg.sender, previousStatus, rec.status);
        if (rec.status == NodeStatus.Unregistered) {
            emit Deregistered(msg.sender);
        }
    }

    // --- Verification ---

    /// @notice Records a region-verification attestation outcome against
    /// `node`'s registry entry. The verification/attestation mechanism
    /// itself is out of scope for this issue (FR-2) — this function only
    /// records a pre-computed pass/fail outcome supplied by the authorized
    /// verifier role (config-supplied address).
    function recordVerification(address node, bool passed, uint256 epoch) external onlyVerifierRole whenNotPaused {
        NodeRecord storage rec = _registryStorage().records[node];
        if (rec.status == NodeStatus.Unregistered || rec.status == NodeStatus.Banned) {
            revert NotRegistered(node);
        }

        NodeStatus previous = rec.status;
        if (passed) {
            rec.lastVerifiedEpoch = epoch;
            if (rec.status == NodeStatus.PendingVerification || rec.status == NodeStatus.Suspended) {
                rec.status = NodeStatus.Active;
            }
        } else if (rec.status == NodeStatus.Active) {
            rec.status = NodeStatus.Suspended;
        }

        emit VerificationRecorded(node, passed, epoch);
        if (previous != rec.status) {
            emit StatusChanged(node, previous, rec.status);
        }
    }

    // --- Overcommitment (pull-based check, per Arch: any keeper may call) ---

    /// @notice Permissionless keeper function: anyone may call this to check
    /// whether `node`'s current stake still satisfies the minimum for its
    /// declared type/capacity. If not, reports an Overcommitment violation
    /// to the SlashingController. Pull-based rather than checked on every
    /// state-mutating call, to keep registration/top-up gas bounded.
    function checkOvercommitment(address node) external whenNotPaused {
        NodeRecord storage rec = _registryStorage().records[node];
        if (rec.status != NodeStatus.Active && rec.status != NodeStatus.Suspended) {
            revert NotRegistered(node);
        }
        uint256 required = minStakeFor(rec.nodeType, rec.capacity);
        if (rec.stake >= required) revert NoOvercommitment(node);

        emit OvercommitmentDetected(node, rec.stake, required);
        address controller = config().getAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER);
        ISlashingController(controller).reportNonHardwareViolation(node, ISlashingController.ViolationType.Overcommitment);
    }

    // --- SlashingController-only hooks ---

    function applySlash(address node, uint256 amount, address recipient) external onlySlashingController {
        NodeRecord storage rec = _registryStorage().records[node];
        uint256 previous = rec.stake;
        uint256 applied = amount >= previous ? previous : amount;
        rec.stake = previous - applied;
        emit StakeChanged(node, previous, rec.stake);

        if (recipient != address(0) && applied > 0) {
            IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
            token.safeTransfer(recipient, applied);
        }
    }

    function banNode(address node, uint256 banExpiry) external onlySlashingController {
        NodeRecord storage rec = _registryStorage().records[node];
        NodeStatus previous = rec.status;
        rec.status = NodeStatus.Banned;
        rec.banExpiry = banExpiry;
        emit StatusChanged(node, previous, NodeStatus.Banned);
    }

    // --- ComputePenaltyStateMachine-only hook (issue #91) ---

    /// @notice Forced removal from the active registry on Stage 4 (Data
    /// Loss) of the compute-specific penalty state machine. Unlike
    /// `banNode`, `banExpiry` is set to `block.timestamp` (not a forward
    /// ban duration) -- issue #91's Stage 4 requires re-registration to
    /// rejoin with state reset to a clean Stage 0, not a timed ban, so the
    /// node is immediately eligible to call `register` again (which
    /// overwrites this record with a fresh entry).
    function forceDeregister(address node) external onlyComputePenaltyStateMachine {
        NodeRecord storage rec = _registryStorage().records[node];
        NodeStatus previous = rec.status;
        rec.status = NodeStatus.Banned;
        rec.banExpiry = block.timestamp;
        emit StatusChanged(node, previous, NodeStatus.Banned);
    }
}
