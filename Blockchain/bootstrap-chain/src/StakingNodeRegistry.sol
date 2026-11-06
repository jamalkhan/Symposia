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
///
/// #159 Node Services: a node registers each Node Service separately, and
/// each (node, service) pair has its own capacity, stake, status,
/// verification, unstake cooldown and ban. The pre-#159 single-type
/// `records` mapping is read-only and migrates into `services` on first
/// write (or via `migrateLegacy`). Node-level functions keep their old
/// meaning for single-service nodes: `stakeOf` totals, `typeOf` is the
/// primary (first-registered) service, `topUp`/`requestUnstake`/
/// `withdrawUnstake` act on the primary service, `recordVerification` and
/// `banNode` apply to every service, and `applySlash` splits pro rata.
contract StakingNodeRegistry is GovernedUpgradeable, INodeRegistry {
    using SafeERC20 for IERC20;

    struct NodeRecord {
        NodeType nodeType; // the Node Service this record is for
        uint256 capacity;
        bytes32 region;
        uint256 stake;
        NodeStatus status;
        uint256 lastVerifiedEpoch;
        uint256 unstakeCooldownEnd;
        uint256 pendingUnstakeAmount;
        uint256 banExpiry;
    }

    /// @notice Why a service's stake changed (#159/#161 ledger).
    enum StakeChangeReason {
        Register,
        TopUp,
        Withdraw,
        Slash
    }

    /// @custom:storage-location erc7201:symposia.StakingNodeRegistry
    struct RegistryStorage {
        // Pre-#159 single-type records. Read-only: migrated into `services`.
        mapping(address => NodeRecord) records;
        // #159: per-(node, service) records, registration order, and the
        // primary service (+1; 0 = none). Service list entries are never
        // removed (bounded by the enum), so history and reward accounts stay stable.
        mapping(address => mapping(uint8 => NodeRecord)) services;
        mapping(address => uint8[]) serviceList;
        mapping(address => uint8) primaryPlusOne;
        mapping(address => bool) migrated;
        // Node-level ban: blocks registering any service until it expires.
        mapping(address => uint256) nodeBanExpiry;
    }

    bytes32 private constant REGISTRY_STORAGE_LOCATION = keccak256("symposia.storage.StakingNodeRegistry");

    event Registered(address indexed node, NodeType nodeType, uint256 capacity, bytes32 region, uint256 stake);
    event StakeChanged(address indexed node, uint256 previousStake, uint256 newStake);
    event StatusChanged(address indexed node, NodeStatus previousStatus, NodeStatus newStatus);
    event Deregistered(address indexed node);
    event UnstakeRequested(address indexed node, uint256 amount, uint256 cooldownEnd);
    event VerificationRecorded(address indexed node, bool passed, uint256 epoch);
    event OvercommitmentDetected(address indexed node, uint256 stake, uint256 required);

    // #159 per-service events (node first, indexed, for the #161 ledger).
    event ServiceRegistered(address indexed node, NodeType indexed service, uint256 capacity, bytes32 region, uint256 stake);
    event ServiceStakeChanged(
        address indexed node, NodeType indexed service, uint256 previousStake, uint256 newStake, StakeChangeReason reason
    );
    event ServiceStatusChanged(address indexed node, NodeType indexed service, NodeStatus previousStatus, NodeStatus newStatus);
    event ServiceUnstakeRequested(address indexed node, NodeType indexed service, uint256 amount, uint256 cooldownEnd);
    event LegacyRecordMigrated(address indexed node, NodeType service);

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
    error ServiceAlreadyRegistered(address node, NodeType service);
    error ServiceNotRegistered(address node, NodeType service);

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

    // --- Legacy migration ---

    /// @notice Moves a pre-#159 single-type record into the per-service
    /// layout. Permissionless and idempotent; every write path does it too.
    function migrateLegacy(address node) public {
        _migrate(node);
    }

    function _migrate(address node) internal {
        RegistryStorage storage $ = _registryStorage();
        if ($.migrated[node]) return;
        $.migrated[node] = true;
        NodeRecord memory legacy = $.records[node];
        if (legacy.status == NodeStatus.Unregistered) return;
        uint8 s = uint8(legacy.nodeType);
        $.services[node][s] = legacy;
        $.serviceList[node].push(s);
        $.primaryPlusOne[node] = s + 1;
        if (legacy.status == NodeStatus.Banned) $.nodeBanExpiry[node] = legacy.banExpiry;
        delete $.records[node];
        emit LegacyRecordMigrated(node, legacy.nodeType);
    }

    /// @dev Read path that also sees a not-yet-migrated legacy record.
    function _view(address node, uint8 s) internal view returns (NodeRecord memory) {
        RegistryStorage storage $ = _registryStorage();
        if (!$.migrated[node]) {
            NodeRecord memory legacy = $.records[node];
            if (legacy.status != NodeStatus.Unregistered && uint8(legacy.nodeType) == s) return legacy;
            NodeRecord memory empty;
            empty.nodeType = NodeType(s);
            return empty;
        }
        return $.services[node][s];
    }

    function _list(address node) internal view returns (uint8[] memory list) {
        RegistryStorage storage $ = _registryStorage();
        if (!$.migrated[node]) {
            NodeRecord memory legacy = $.records[node];
            if (legacy.status == NodeStatus.Unregistered) return new uint8[](0);
            list = new uint8[](1);
            list[0] = uint8(legacy.nodeType);
            return list;
        }
        return $.serviceList[node];
    }

    function _primary(address node) internal view returns (bool has, uint8 s) {
        RegistryStorage storage $ = _registryStorage();
        if (!$.migrated[node]) {
            NodeRecord memory legacy = $.records[node];
            return (legacy.status != NodeStatus.Unregistered, uint8(legacy.nodeType));
        }
        uint8 p = $.primaryPlusOne[node];
        return (p != 0, p == 0 ? 0 : p - 1);
    }

    // --- Views (INodeRegistry) ---

    function statusOf(address node) public view returns (NodeStatus) {
        uint8[] memory list = _list(node);
        // Ranked: Active > PendingVerification > Suspended > Deregistering > Banned > Unregistered.
        uint8 bestRank;
        NodeStatus best = NodeStatus.Unregistered;
        for (uint256 i; i < list.length; i++) {
            NodeStatus st = _view(node, list[i]).status;
            uint8 rank = _statusRank(st);
            if (rank > bestRank) {
                bestRank = rank;
                best = st;
            }
        }
        if (best == NodeStatus.Unregistered && _registryStorage().nodeBanExpiry[node] != 0) return NodeStatus.Banned;
        return best;
    }

    function _statusRank(NodeStatus st) private pure returns (uint8) {
        if (st == NodeStatus.Active) return 5;
        if (st == NodeStatus.PendingVerification) return 4;
        if (st == NodeStatus.Suspended) return 3;
        if (st == NodeStatus.Deregistering) return 2;
        if (st == NodeStatus.Banned) return 1;
        return 0;
    }

    function typeOf(address node) external view returns (NodeType) {
        (, uint8 s) = _primary(node);
        return NodeType(s);
    }

    function stakeOf(address node) public view returns (uint256) {
        return totalStakeOf(node);
    }

    function totalStakeOf(address node) public view returns (uint256 total) {
        uint8[] memory list = _list(node);
        for (uint256 i; i < list.length; i++) {
            total += _view(node, list[i]).stake;
        }
    }

    function lastVerifiedEpochOf(address node) external view returns (uint256) {
        (, uint8 s) = _primary(node);
        return _view(node, s).lastVerifiedEpoch;
    }

    /// @notice The primary service's record (the whole node's record before #159).
    function recordOf(address node) external view returns (NodeRecord memory) {
        (, uint8 s) = _primary(node);
        return _view(node, s);
    }

    function servicesOf(address node) external view returns (NodeType[] memory out) {
        uint8[] memory list = _list(node);
        out = new NodeType[](list.length);
        for (uint256 i; i < list.length; i++) {
            out[i] = NodeType(list[i]);
        }
    }

    function hasPrimaryService(address node) external view returns (bool has) {
        (has,) = _primary(node);
    }

    function serviceRecordOf(address node, NodeType service) external view returns (NodeRecord memory) {
        return _view(node, uint8(service));
    }

    function serviceStatusOf(address node, NodeType service) external view returns (NodeStatus) {
        return _view(node, uint8(service)).status;
    }

    function serviceStakeOf(address node, NodeType service) external view returns (uint256) {
        return _view(node, uint8(service)).stake;
    }

    function serviceLastVerifiedEpochOf(address node, NodeType service) external view returns (uint256) {
        return _view(node, uint8(service)).lastVerifiedEpoch;
    }

    /// @notice `base + perUnit * capacity`, both read live from config,
    /// keyed by Node Service (FR-2, #159). Reverts on overflow rather than wrapping.
    function minStakeFor(NodeType nodeType, uint256 capacity) public view returns (uint256) {
        uint256 base = config().getUint(ConfigKeys.registryStakeBase(uint8(nodeType)));
        uint256 perUnit = config().getUint(ConfigKeys.registryStakePerUnit(uint8(nodeType)));
        return base + (perUnit * capacity);
    }

    // --- Registration ---

    /// @notice Pre-#159 entry point: registers `nodeType` as a Node Service.
    function register(NodeType nodeType, uint256 capacity, bytes32 region, uint256 stakeAmount) external whenNotPaused {
        _registerService(nodeType, capacity, region, stakeAmount);
    }

    /// @notice Registers one Node Service for the caller's node with its own stake (#159).
    function registerService(NodeType service, uint256 capacity, bytes32 region, uint256 stakeAmount)
        external
        whenNotPaused
    {
        _registerService(service, capacity, region, stakeAmount);
    }

    function _registerService(NodeType service, uint256 capacity, bytes32 region, uint256 stakeAmount) internal {
        address node = msg.sender;
        _migrate(node);
        RegistryStorage storage $ = _registryStorage();
        uint8 s = uint8(service);
        NodeRecord storage rec = $.services[node][s];

        if (
            rec.status == NodeStatus.Active || rec.status == NodeStatus.PendingVerification
                || rec.status == NodeStatus.Suspended || rec.status == NodeStatus.Deregistering
        ) {
            // Pre-#159 message for single-service callers; ServiceAlreadyRegistered when the node runs others.
            if ($.primaryPlusOne[node] == s + 1) revert AlreadyRegistered(node);
            revert ServiceAlreadyRegistered(node, service);
        }
        if (rec.status == NodeStatus.Banned && block.timestamp < rec.banExpiry) {
            revert StillBanned(node, rec.banExpiry);
        }
        uint256 nodeBan = $.nodeBanExpiry[node];
        if (nodeBan != 0 && block.timestamp < nodeBan) revert StillBanned(node, nodeBan);

        uint256 required = minStakeFor(service, capacity);
        if (stakeAmount < required) revert InsufficientStake(stakeAmount, required);

        IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
        token.safeTransferFrom(node, address(this), stakeAmount);

        bool listed = _inList(node, s);
        // Fresh registration entry — overwrites any prior (expired-ban or
        // fully-deregistered) record for this service, per FR-2's "reject
        // reactivation ... without a fresh registration flow" requirement.
        $.services[node][s] = NodeRecord({
            nodeType: service,
            capacity: capacity,
            region: region,
            stake: stakeAmount,
            status: NodeStatus.PendingVerification,
            lastVerifiedEpoch: 0,
            unstakeCooldownEnd: 0,
            pendingUnstakeAmount: 0,
            banExpiry: 0
        });
        if (!listed) $.serviceList[node].push(s);
        if ($.primaryPlusOne[node] == 0) $.primaryPlusOne[node] = s + 1;

        emit Registered(node, service, capacity, region, stakeAmount);
        emit StatusChanged(node, NodeStatus.Unregistered, NodeStatus.PendingVerification);
        emit ServiceRegistered(node, service, capacity, region, stakeAmount);
        emit ServiceStakeChanged(node, service, 0, stakeAmount, StakeChangeReason.Register);
        emit ServiceStatusChanged(node, service, NodeStatus.Unregistered, NodeStatus.PendingVerification);
    }

    function _inList(address node, uint8 s) internal view returns (bool) {
        uint8[] storage list = _registryStorage().serviceList[node];
        for (uint256 i; i < list.length; i++) {
            if (list[i] == s) return true;
        }
        return false;
    }

    function _primaryService(address node) internal view returns (NodeType) {
        uint8 p = _registryStorage().primaryPlusOne[node];
        if (p == 0) revert NotRegistered(node);
        return NodeType(p - 1);
    }

    /// @notice Any address may top up a registered node's primary service stake (FR-2).
    function topUp(address node, uint256 amount) external whenNotPaused {
        _migrate(node);
        _topUp(node, _primaryService(node), amount);
    }

    /// @notice Any address may top up one of a node's services (#159).
    function topUpService(address node, NodeType service, uint256 amount) external whenNotPaused {
        _migrate(node);
        _topUp(node, service, amount);
    }

    function _topUp(address node, NodeType service, uint256 amount) internal {
        NodeRecord storage rec = _registryStorage().services[node][uint8(service)];
        if (rec.status == NodeStatus.Unregistered || rec.status == NodeStatus.Banned) {
            revert NotRegistered(node);
        }

        IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
        token.safeTransferFrom(msg.sender, address(this), amount);

        uint256 previous = rec.stake;
        rec.stake = previous + amount;
        emit StakeChanged(node, previous, rec.stake);
        emit ServiceStakeChanged(node, service, previous, rec.stake, StakeChangeReason.TopUp);
    }

    // --- Unstaking ---

    /// @notice Requests withdrawal of `amount` of the caller's primary service stake.
    /// Partial unstake is only allowed if the remaining stake stays at or
    /// above the current minimum for the service's declared capacity.
    function requestUnstake(uint256 amount) external whenNotPaused {
        _migrate(msg.sender);
        _requestUnstake(msg.sender, _primaryService(msg.sender), amount);
    }

    /// @notice Per-service unstake request; the node's other services are untouched (#159).
    function requestServiceUnstake(NodeType service, uint256 amount) external whenNotPaused {
        _migrate(msg.sender);
        _requestUnstake(msg.sender, service, amount);
    }

    function _requestUnstake(address node, NodeType service, uint256 amount) internal {
        NodeRecord storage rec = _registryStorage().services[node][uint8(service)];
        if (rec.status == NodeStatus.Unregistered || rec.status == NodeStatus.Banned) {
            revert NotRegistered(node);
        }
        if (amount > rec.stake) revert InsufficientStake(rec.stake, amount);

        uint256 remaining = rec.stake - amount;
        if (remaining != 0) {
            uint256 required = minStakeFor(service, rec.capacity);
            if (remaining < required) revert RemainingBelowMinimum(remaining, required);
        }

        uint256 cooldown = config().getUint(ConfigKeys.REGISTRY_UNSTAKE_COOLDOWN);
        rec.pendingUnstakeAmount = amount;
        rec.unstakeCooldownEnd = block.timestamp + cooldown;

        NodeStatus previous = rec.status;
        rec.status = NodeStatus.Deregistering;
        emit UnstakeRequested(node, amount, rec.unstakeCooldownEnd);
        emit StatusChanged(node, previous, NodeStatus.Deregistering);
        emit ServiceUnstakeRequested(node, service, amount, rec.unstakeCooldownEnd);
        emit ServiceStatusChanged(node, service, previous, NodeStatus.Deregistering);
    }

    /// @notice Completes a previously requested primary-service unstake once
    /// its cooldown has elapsed, transferring the pending amount back to the caller.
    function withdrawUnstake() external whenNotPaused {
        _migrate(msg.sender);
        _withdrawUnstake(msg.sender, _primaryService(msg.sender));
    }

    function withdrawServiceUnstake(NodeType service) external whenNotPaused {
        _migrate(msg.sender);
        _withdrawUnstake(msg.sender, service);
    }

    function _withdrawUnstake(address node, NodeType service) internal {
        NodeRecord storage rec = _registryStorage().services[node][uint8(service)];
        if (rec.pendingUnstakeAmount == 0) revert NoPendingUnstake();
        if (block.timestamp < rec.unstakeCooldownEnd) revert CooldownNotElapsed(rec.unstakeCooldownEnd);

        uint256 amount = rec.pendingUnstakeAmount;
        uint256 previousStake = rec.stake;
        rec.stake = previousStake - amount;
        rec.pendingUnstakeAmount = 0;
        rec.unstakeCooldownEnd = 0;

        NodeStatus previousStatus = rec.status;
        rec.status = rec.stake == 0 ? NodeStatus.Unregistered : NodeStatus.Active;
        _clearPrimaryIfIdle(node);

        IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
        token.safeTransfer(node, amount);

        emit StakeChanged(node, previousStake, rec.stake);
        emit StatusChanged(node, previousStatus, rec.status);
        emit ServiceStakeChanged(node, service, previousStake, rec.stake, StakeChangeReason.Withdraw);
        emit ServiceStatusChanged(node, service, previousStatus, rec.status);
        if (rec.status == NodeStatus.Unregistered) {
            emit Deregistered(node);
        }
    }

    /// @dev Primary (and so reward accounts) only resets once the node runs nothing.
    function _clearPrimaryIfIdle(address node) internal {
        RegistryStorage storage $ = _registryStorage();
        uint8[] storage list = $.serviceList[node];
        for (uint256 i; i < list.length; i++) {
            if ($.services[node][list[i]].status != NodeStatus.Unregistered) return;
        }
        $.primaryPlusOne[node] = 0;
    }

    // --- Verification ---

    /// @notice Records a region-verification attestation outcome. Region is a
    /// fact about the node, so it applies to every registered service (#159).
    /// The verification/attestation mechanism itself is out of scope (FR-2) —
    /// this only records a pre-computed pass/fail outcome from the verifier role.
    function recordVerification(address node, bool passed, uint256 epoch) external onlyVerifierRole whenNotPaused {
        _migrate(node);
        uint8[] memory list = _registryStorage().serviceList[node];
        bool any;
        for (uint256 i; i < list.length; i++) {
            NodeStatus st = _registryStorage().services[node][list[i]].status;
            if (st == NodeStatus.Unregistered || st == NodeStatus.Banned) continue;
            any = true;
            _recordVerification(node, NodeType(list[i]), passed, epoch);
        }
        if (!any) revert NotRegistered(node);
        emit VerificationRecorded(node, passed, epoch);
    }

    function recordServiceVerification(address node, NodeType service, bool passed, uint256 epoch)
        external
        onlyVerifierRole
        whenNotPaused
    {
        _migrate(node);
        NodeStatus st = _registryStorage().services[node][uint8(service)].status;
        if (st == NodeStatus.Unregistered || st == NodeStatus.Banned) revert NotRegistered(node);
        _recordVerification(node, service, passed, epoch);
        emit VerificationRecorded(node, passed, epoch);
    }

    function _recordVerification(address node, NodeType service, bool passed, uint256 epoch) internal {
        NodeRecord storage rec = _registryStorage().services[node][uint8(service)];
        NodeStatus previous = rec.status;
        if (passed) {
            rec.lastVerifiedEpoch = epoch;
            if (rec.status == NodeStatus.PendingVerification || rec.status == NodeStatus.Suspended) {
                rec.status = NodeStatus.Active;
            }
        } else if (rec.status == NodeStatus.Active) {
            rec.status = NodeStatus.Suspended;
        }
        if (previous != rec.status) {
            emit StatusChanged(node, previous, rec.status);
            emit ServiceStatusChanged(node, service, previous, rec.status);
        }
    }

    // --- Overcommitment (pull-based check, per Arch: any keeper may call) ---

    /// @notice Permissionless keeper function: reports an Overcommitment
    /// violation if any of `node`'s active services is staked below its
    /// minimum. Pull-based rather than checked on every state-mutating call,
    /// to keep registration/top-up gas bounded.
    function checkOvercommitment(address node) external whenNotPaused {
        _migrate(node);
        uint8[] memory list = _registryStorage().serviceList[node];
        for (uint256 i; i < list.length; i++) {
            if (_overcommitted(node, list[i])) return _reportOvercommitment(node, list[i]);
        }
        if (statusOf(node) != NodeStatus.Active && statusOf(node) != NodeStatus.Suspended) revert NotRegistered(node);
        revert NoOvercommitment(node);
    }

    /// @notice Same check for one service (#159).
    function checkServiceOvercommitment(address node, NodeType service) external whenNotPaused {
        _migrate(node);
        NodeStatus st = _registryStorage().services[node][uint8(service)].status;
        if (st != NodeStatus.Active && st != NodeStatus.Suspended) revert NotRegistered(node);
        if (!_overcommitted(node, uint8(service))) revert NoOvercommitment(node);
        _reportOvercommitment(node, uint8(service));
    }

    function _overcommitted(address node, uint8 s) internal view returns (bool) {
        NodeRecord storage rec = _registryStorage().services[node][s];
        if (rec.status != NodeStatus.Active && rec.status != NodeStatus.Suspended) return false;
        return rec.stake < minStakeFor(NodeType(s), rec.capacity);
    }

    function _reportOvercommitment(address node, uint8 s) internal {
        NodeRecord storage rec = _registryStorage().services[node][s];
        emit OvercommitmentDetected(node, rec.stake, minStakeFor(NodeType(s), rec.capacity));
        address controller = config().getAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER);
        ISlashingController(controller).reportNonHardwareViolation(node, ISlashingController.ViolationType.Overcommitment);
    }

    // --- SlashingController-only hooks ---

    /// @notice Node-level slash: split across the node's services in proportion
    /// to stake (the last staked service takes the rounding dust), so the total
    /// slashed is exactly `min(amount, totalStake)`.
    function applySlash(address node, uint256 amount, address recipient) external onlySlashingController {
        _migrate(node);
        RegistryStorage storage $ = _registryStorage();
        uint8[] memory list = $.serviceList[node];
        uint256 total = totalStakeOf(node);
        uint256 target = amount >= total ? total : amount;
        if (target == 0) return;
        uint256 lastStaked;
        for (uint256 i; i < list.length; i++) {
            if ($.services[node][list[i]].stake > 0) lastStaked = i;
        }
        uint256 remaining = target;
        uint256 applied;
        for (uint256 i; i < list.length && remaining > 0; i++) {
            uint256 st = $.services[node][list[i]].stake;
            if (st == 0) continue;
            uint256 part = i == lastStaked ? remaining : (target * st) / total;
            if (part > st) part = st;
            applied += _slashService(node, list[i], part);
            remaining -= part;
        }
        _sendSlashed(recipient, applied);
    }

    function applyServiceSlash(address node, NodeType service, uint256 amount, address recipient)
        external
        onlySlashingController
    {
        _migrate(node);
        uint256 st = _registryStorage().services[node][uint8(service)].stake;
        uint256 applied = _slashService(node, uint8(service), amount >= st ? st : amount);
        _sendSlashed(recipient, applied);
    }

    function _slashService(address node, uint8 s, uint256 applied) internal returns (uint256) {
        NodeRecord storage rec = _registryStorage().services[node][s];
        uint256 previous = rec.stake;
        rec.stake = previous - applied;
        emit StakeChanged(node, previous, rec.stake);
        emit ServiceStakeChanged(node, NodeType(s), previous, rec.stake, StakeChangeReason.Slash);
        return applied;
    }

    function _sendSlashed(address recipient, uint256 applied) internal {
        if (recipient != address(0) && applied > 0) {
            IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
            token.safeTransfer(recipient, applied);
        }
    }

    /// @notice Node-level ban: every service is Banned and no service can be
    /// registered until `banExpiry`.
    function banNode(address node, uint256 banExpiry) external onlySlashingController {
        _migrate(node);
        RegistryStorage storage $ = _registryStorage();
        NodeStatus previous = statusOf(node);
        $.nodeBanExpiry[node] = banExpiry;
        uint8[] memory list = $.serviceList[node];
        for (uint256 i; i < list.length; i++) {
            _banService(node, list[i], banExpiry, false);
        }
        emit StatusChanged(node, previous, NodeStatus.Banned);
    }

    /// @notice Bans one service only (#159).
    function banService(address node, NodeType service, uint256 banExpiry) external onlySlashingController {
        _migrate(node);
        _banService(node, uint8(service), banExpiry, true);
    }

    function _banService(address node, uint8 s, uint256 banExpiry, bool emitNodeLevel) internal {
        RegistryStorage storage $ = _registryStorage();
        NodeRecord storage rec = $.services[node][s];
        NodeStatus previous = rec.status;
        if (previous == NodeStatus.Unregistered && rec.stake == 0 && !emitNodeLevel) return;
        if (!_inList(node, s)) {
            rec.nodeType = NodeType(s);
            $.serviceList[node].push(s);
            if ($.primaryPlusOne[node] == 0) $.primaryPlusOne[node] = s + 1;
        }
        rec.status = NodeStatus.Banned;
        rec.banExpiry = banExpiry;
        if (emitNodeLevel) emit StatusChanged(node, previous, NodeStatus.Banned);
        emit ServiceStatusChanged(node, NodeType(s), previous, NodeStatus.Banned);
    }

    // --- ComputePenaltyStateMachine-only hook (issue #91) ---

    /// @notice Forced removal of the **Compute** service on Stage 4 (Data
    /// Loss) of the compute-specific penalty state machine (#159: the node's
    /// other services keep running). Unlike `banNode`, `banExpiry` is set to
    /// `block.timestamp` (not a forward ban duration) -- issue #91's Stage 4
    /// requires re-registration to rejoin with state reset to a clean
    /// Stage 0, not a timed ban, so the service is immediately eligible to be
    /// registered again (which overwrites this record with a fresh entry).
    function forceDeregister(address node) external onlyComputePenaltyStateMachine {
        _migrate(node);
        _banService(node, uint8(NodeType.Compute), block.timestamp, true);
    }
}
