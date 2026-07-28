// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {MessageHashUtils} from "@openzeppelin/contracts/utils/cryptography/MessageHashUtils.sol";
import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";
import {ISlashingController} from "./interfaces/ISlashingController.sol";

/// @title SlashingController
/// @notice Implements FR-5 of issue #52: the progressive penalty model
/// (Stages 1-4) plus non-hardware violations, driven by config-supplied
/// percentages/caps/durations and an authorized fault-attestation input.
/// Off-chain fault detection/verifier consensus is out of scope — this
/// contract defines the trigger interface (`submitFaultAttestation`, a
/// signed oracle report; `reportNonHardwareViolation`, called directly by
/// the registry on its own detected invariant violation) and applies the
/// resulting stake reduction.
contract SlashingController is GovernedUpgradeable, ISlashingController {
    uint8 internal constant STAGE_NONE = 0;
    uint8 internal constant STAGE_1_WARNING = 1;
    uint8 internal constant STAGE_2_DEGRADED = 2;
    uint8 internal constant STAGE_3_LOW_RATE = 3;
    uint8 internal constant STAGE_4_HIGH_RATE = 4;

    struct NodePenaltyState {
        uint8 stage;
        uint256 cumulativeStage3SlashBps;
        uint256 stage3BaselineStake;
        uint256 consecutiveCleanEpochs;
        bool stage4Confirmed;
    }

    /// @custom:storage-location erc7201:symposia.SlashingController
    struct SlashingStorage {
        mapping(address => NodePenaltyState) states;
    }

    bytes32 private constant SLASHING_STORAGE_LOCATION = keccak256("symposia.storage.SlashingController");

    event StageTransition(address indexed node, uint8 previousStage, uint8 newStage, uint8 severity);
    event Slashed(address indexed node, uint256 amount, uint256 appliedBps, uint8 stage);
    event Recovered(address indexed node);
    event NonHardwareViolationSlashed(address indexed node, ViolationType violationType, uint256 amount, uint256 banExpiry);
    event StakeCommitmentViolationSlashed(address indexed node, bytes32 reason, uint256 amount, uint256 banExpiry);

    error InvalidSignature();
    error NotRegistry(address caller);
    error InvalidStage(uint8 stage);
    error NotFoundationRegistry(address caller);
    error NotComputePenaltyStateMachine(address caller);

    function _slashingStorage() private pure returns (SlashingStorage storage $) {
        bytes32 slot = SLASHING_STORAGE_LOCATION;
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

    function _registry() internal view returns (INodeRegistry) {
        return INodeRegistry(config().getAddress(ConfigKeys.REGISTRY_ADDRESS));
    }

    modifier onlyRegistry() {
        if (msg.sender != config().getAddress(ConfigKeys.REGISTRY_ADDRESS)) revert NotRegistry(msg.sender);
        _;
    }

    modifier onlyFoundationRegistry() {
        if (msg.sender != config().getAddress(ConfigKeys.FOUNDATION_REGISTRY_ADDRESS)) {
            revert NotFoundationRegistry(msg.sender);
        }
        _;
    }

    modifier onlyComputePenaltyStateMachine() {
        if (msg.sender != config().getAddress(ConfigKeys.COMPUTE_PENALTY_STATE_MACHINE_ADDRESS)) {
            revert NotComputePenaltyStateMachine(msg.sender);
        }
        _;
    }

    function stageOf(address node) external view returns (uint8) {
        return _slashingStorage().states[node].stage;
    }

    function stateOf(address node) external view returns (NodePenaltyState memory) {
        return _slashingStorage().states[node];
    }

    // --- Progressive stage fault attestation (signed oracle input) ---

    /// @notice Applies a confirmed fault-attestation for `node` at penalty
    /// `stage` (0 = clean/no-fault epoch report, used to accrue recovery
    /// progress; 1-4 = the progressive stages from
    /// `node-runner-incentives-and-penalties.md`). Verified against the
    /// config-supplied `slashing.fault.signer` address.
    function submitFaultAttestation(address node, uint8 stage, uint256 epoch, bool auditPassed, bytes calldata signature)
        external
        whenNotPaused
    {
        if (stage > STAGE_4_HIGH_RATE) revert InvalidStage(stage);

        bytes32 digest = MessageHashUtils.toEthSignedMessageHash(
            keccak256(abi.encode(address(this), node, stage, epoch, auditPassed))
        );
        address signer = ECDSA.recover(digest, signature);
        if (signer != config().getAddress(ConfigKeys.SLASHING_FAULT_SIGNER)) revert InvalidSignature();

        if (stage == STAGE_NONE) {
            _processCleanEpoch(node, auditPassed);
        } else {
            _processFault(node, stage);
        }
    }

    function _processCleanEpoch(address node, bool auditPassed) internal {
        NodePenaltyState storage s = _slashingStorage().states[node];
        if (s.stage == STAGE_NONE) return;

        s.consecutiveCleanEpochs += 1;
        uint256 required = config().getUint(ConfigKeys.SLASHING_RECOVERY_CLEAN_EPOCHS);
        if (auditPassed && s.stage != STAGE_4_HIGH_RATE && s.consecutiveCleanEpochs >= required) {
            uint8 previous = s.stage;
            s.stage = STAGE_NONE;
            s.consecutiveCleanEpochs = 0;
            emit StageTransition(node, previous, STAGE_NONE, 0 /* INFO */ );
            emit Recovered(node);
        }
    }

    function _processFault(address node, uint8 stage) internal {
        NodePenaltyState storage s = _slashingStorage().states[node];
        uint8 previous = s.stage;
        s.consecutiveCleanEpochs = 0;

        if (stage == STAGE_1_WARNING || stage == STAGE_2_DEGRADED) {
            s.stage = stage;
            emit StageTransition(node, previous, stage, 1 /* WARNING */ );
            return;
        }

        if (stage == STAGE_3_LOW_RATE) {
            // The cumulative Stage 3 cap is expressed as a percentage of the
            // node's stake at the moment it *entered* Stage 3 (not a
            // compounding percentage of whatever the stake has shrunk to by
            // each subsequent epoch) — this baseline is (re)established the
            // first time a node enters Stage 3, per the worked example in
            // `node-runner-incentives-and-penalties.md` (5%/epoch up to a
            // flat 25% cumulative cap).
            if (previous != STAGE_3_LOW_RATE) {
                s.stage3BaselineStake = _registry().stakeOf(node);
                s.cumulativeStage3SlashBps = 0;
            }

            uint256 pctBps = config().getUint(ConfigKeys.SLASHING_STAGE3_PCT_PER_EPOCH_BPS);
            uint256 capBps = config().getUint(ConfigKeys.SLASHING_STAGE3_CAP_BPS);
            uint256 remainingCap = capBps > s.cumulativeStage3SlashBps ? capBps - s.cumulativeStage3SlashBps : 0;
            uint256 appliedBps = pctBps > remainingCap ? remainingCap : pctBps;

            s.cumulativeStage3SlashBps += appliedBps;
            s.stage = STAGE_3_LOW_RATE;

            uint256 amount = (s.stage3BaselineStake * appliedBps) / 10_000;
            _applySlashAmount(node, amount);
            emit Slashed(node, amount, appliedBps, STAGE_3_LOW_RATE);
            emit StageTransition(node, previous, STAGE_3_LOW_RATE, 2 /* CRITICAL */ );
            return;
        }

        // stage == STAGE_4_HIGH_RATE
        uint256 bps = s.stage4Confirmed
            ? config().getUint(ConfigKeys.SLASHING_STAGE4_ONGOING_BPS)
            : config().getUint(ConfigKeys.SLASHING_STAGE4_IMMEDIATE_BPS);

        s.stage = STAGE_4_HIGH_RATE;
        uint256 slashedAmount = _slashBps(node, bps);
        emit Slashed(node, slashedAmount, bps, STAGE_4_HIGH_RATE);
        emit StageTransition(node, previous, STAGE_4_HIGH_RATE, 3 /* EMERGENCY */ );

        if (!s.stage4Confirmed) {
            s.stage4Confirmed = true;
            uint256 banDuration = config().getUint(ConfigKeys.SLASHING_BAN_DURATION);
            _registry().banNode(node, block.timestamp + banDuration);
        }
    }

    // --- Non-hardware violations (registry-triggered, bypass progressive stages) ---

    function reportNonHardwareViolation(address node, ViolationType violationType) external onlyRegistry whenNotPaused {
        NodePenaltyState storage s = _slashingStorage().states[node];
        uint8 previous = s.stage;
        s.stage = STAGE_4_HIGH_RATE;
        s.consecutiveCleanEpochs = 0;
        s.stage4Confirmed = true;

        uint256 pctBps = config().getUint(ConfigKeys.slashingViolationPctBps(uint8(violationType)));
        uint256 amount = _slashBps(node, pctBps);

        uint256 banDuration = config().getUint(ConfigKeys.SLASHING_BAN_DURATION);
        uint256 banExpiry = block.timestamp + banDuration;
        _registry().banNode(node, banExpiry);

        emit StageTransition(node, previous, STAGE_4_HIGH_RATE, 3 /* EMERGENCY */ );
        emit NonHardwareViolationSlashed(node, violationType, amount, banExpiry);
    }

    // --- Stake-commitment violations (FoundationRegistry-triggered, issue #57) ---

    /// @notice Early-exit-before-floor entry point, restricted to
    /// `FoundationRegistry`. Reuses the same immediate-Stage-4/ban
    /// machinery `reportNonHardwareViolation` uses, but as its own
    /// violation category (`StakeCommitmentViolation`) with its own
    /// config-supplied penalty percentage, so an early-exit penalty cannot
    /// accidentally map onto an unrelated fault category.
    function triggerStakeCommitmentViolation(address node, bytes32 reason)
        external
        onlyFoundationRegistry
        whenNotPaused
    {
        NodePenaltyState storage s = _slashingStorage().states[node];
        uint8 previous = s.stage;
        s.stage = STAGE_4_HIGH_RATE;
        s.consecutiveCleanEpochs = 0;
        s.stage4Confirmed = true;

        uint256 pctBps = config().getUint(ConfigKeys.slashingViolationPctBps(uint8(ViolationType.StakeCommitmentViolation)));
        uint256 amount = _slashBps(node, pctBps);

        uint256 banDuration = config().getUint(ConfigKeys.SLASHING_BAN_DURATION);
        uint256 banExpiry = block.timestamp + banDuration;
        _registry().banNode(node, banExpiry);

        emit StageTransition(node, previous, STAGE_4_HIGH_RATE, 3 /* EMERGENCY */ );
        emit StakeCommitmentViolationSlashed(node, reason, amount, banExpiry);
    }

    // --- Compute penalty state machine hook (issue #91) ---

    function applyComputePenaltySlash(address node, uint256 amount)
        external
        onlyComputePenaltyStateMachine
        whenNotPaused
        returns (uint256)
    {
        return _applySlashAmount(node, amount);
    }

    // --- Token disposition ---

    function _slashBps(address node, uint256 bps) internal returns (uint256 amount) {
        if (bps == 0) return 0;
        uint256 stake = _registry().stakeOf(node);
        amount = (stake * bps) / 10_000;
        return _applySlashAmount(node, amount);
    }

    function _applySlashAmount(address node, uint256 amount) internal returns (uint256) {
        if (amount == 0) return 0;
        address recipient = _dispositionRecipient();
        _registry().applySlash(node, amount, recipient);
        return amount;
    }

    function _dispositionRecipient() internal view returns (address) {
        uint256 disposition = config().getUint(ConfigKeys.SLASHING_DISPOSITION);
        if (disposition == 1) {
            return config().getAddress(ConfigKeys.SLASHING_REDISTRIBUTION_TARGET);
        }
        // Default / disposition == 0: burn. Burning is modeled as sending
        // to the canonical dead-address sink rather than assuming the
        // token contract exposes a `burn(uint256)` callable by a
        // non-holder — keeps this contract agnostic of #50's exact burn
        // ABI while still removing the tokens from circulation.
        return 0x000000000000000000000000000000000000dEaD;
    }
}
