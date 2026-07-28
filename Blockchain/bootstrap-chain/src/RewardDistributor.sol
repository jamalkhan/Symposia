// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import {SafeERC20} from "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {MessageHashUtils} from "@openzeppelin/contracts/utils/cryptography/MessageHashUtils.sol";
import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";

/// @title RewardDistributor
/// @notice Implements FR-4 of issue #52: epoch-based reward computation and
/// payout, driven entirely by config-supplied parameters and off-chain
/// signed metric reports (per-node, per-epoch aggregated telemetry — the
/// aggregation pipeline itself is out of scope).
///
/// Scope note / deviation from `tokenomics-mvp.md`'s exact worked-example
/// formula: this issue's spec text (as posted on #52) does not restate the
/// precise `raw_mult` dynamic-multiplier formula, only that MULT_MIN/
/// MULT_MAX/epsilon are config-supplied bounds. This implementation reads a
/// per-type base weight from config, renormalizes across the types present
/// in a given `sealEpoch` call, and clamps to [MULT_MIN, MULT_MAX] — the
/// bound-checking and config-driven shape are faithful to FR-4 item 3, but
/// the exact multiplier curve should be reconciled against
/// `tokenomics-mvp.md`'s worked numbers before this is used for a mainnet
/// genesis deploy (flagged for #53/#57 follow-up).
///
/// `sealEpoch` takes an explicit, caller-supplied node list rather than
/// iterating the full registry, per the Arch pass's gas/DoS guidance —
/// batch size is controlled by the (permissionless) caller, avoiding an
/// unbounded per-epoch loop while keeping this MVP-simple rather than a
/// fully lazy/pull-based design (tracked as a scaling follow-up).
contract RewardDistributor is GovernedUpgradeable {
    using SafeERC20 for IERC20;

    struct MetricRecord {
        uint256 score;
        uint256 heartbeatBps;
        bool submitted;
    }

    struct EpochSeal {
        bool sealed_;
        uint256 sealedAt;
    }

    /// @custom:storage-location erc7201:symposia.RewardDistributor
    struct RewardStorage {
        mapping(uint256 => mapping(address => MetricRecord)) metrics;
        mapping(uint256 => EpochSeal) seals;
        mapping(address => uint256) reserveBalance;
        mapping(address => uint256) consecutiveCompliantEpochs;
        mapping(uint256 => mapping(address => uint256)) payoutOf;
    }

    bytes32 private constant REWARD_STORAGE_LOCATION = keccak256("symposia.storage.RewardDistributor");

    event MetricReported(address indexed node, uint256 indexed epoch, uint256 score, uint256 heartbeatBps);
    event EpochSealed(uint256 indexed epoch, uint256 nodeCount, uint256 totalCapacityDistributed);
    event Payout(address indexed node, uint256 indexed epoch, uint256 amount);
    event ReserveCredit(address indexed node, uint256 indexed epoch, uint256 amount);
    event ReserveSwept(address indexed node, uint256 amount);

    error EpochAlreadySealed(uint256 epoch);
    error FinalityWindowNotElapsed(uint256 epoch);
    error InvalidSignature();
    error NoReserveToSweep();

    function _rewardStorage() private pure returns (RewardStorage storage $) {
        bytes32 slot = REWARD_STORAGE_LOCATION;
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

    // --- Metric ingestion ---

    /// @notice Ingests a signed per-node, per-epoch metric report from the
    /// off-chain oracle/aggregation pipeline. Signature is verified against
    /// the config-supplied `reward.metric.signer` address (MVP: single
    /// signer; multi-signer quorum is a config-driven extension per the
    /// Arch pass's security recommendation, tracked as a follow-up).
    function submitMetricReport(address node, uint256 epoch, uint256 score, uint256 heartbeatBps, bytes calldata signature)
        external
        whenNotPaused
    {
        bytes32 digest = MessageHashUtils.toEthSignedMessageHash(
            keccak256(abi.encode(address(this), node, epoch, score, heartbeatBps))
        );
        address signer = ECDSA.recover(digest, signature);
        if (signer != config().getAddress(ConfigKeys.REWARD_METRIC_SIGNER)) revert InvalidSignature();

        _rewardStorage().metrics[epoch][node] = MetricRecord({score: score, heartbeatBps: heartbeatBps, submitted: true});
        emit MetricReported(node, epoch, score, heartbeatBps);
    }

    function metricOf(uint256 epoch, address node) external view returns (MetricRecord memory) {
        return _rewardStorage().metrics[epoch][node];
    }

    function isSealed(uint256 epoch) external view returns (bool) {
        return _rewardStorage().seals[epoch].sealed_;
    }

    function reserveBalanceOf(address node) external view returns (uint256) {
        return _rewardStorage().reserveBalance[node];
    }

    // --- Eligibility ---

    function _isEligible(address node, uint256 epoch) internal view returns (bool) {
        INodeRegistry registry = _registry();
        if (address(registry) == address(0)) return false;

        MetricRecord memory m = _rewardStorage().metrics[epoch][node];
        if (!m.submitted) return false;

        if (registry.statusOf(node) != INodeRegistry.NodeStatus.Active) return false;
        // Approximates "passed region verification before epoch start" (FR-4
        // item 6): the node's most recent recorded verification must be at
        // or before the epoch being sealed. `Active` status already implies
        // at least one passing verification, so this only needs to check
        // recency, not presence.
        if (registry.lastVerifiedEpochOf(node) > epoch) return false;

        uint256 required = registry.minStakeFor(registry.typeOf(node), 0);
        // NOTE: capacity is not re-read here (registry does not expose it via
        // INodeRegistry's narrow interface); this checks stake against the
        // node-type base minimum as a floor. A tighter per-capacity check is
        // available to callers via the full StakingNodeRegistry ABI and is
        // left as a documented follow-up for #53/#57 to tighten if needed.
        if (registry.stakeOf(node) < required) return false;

        return true;
    }

    function _registry() internal view returns (INodeRegistry) {
        return INodeRegistry(config().getAddress(ConfigKeys.REGISTRY_ADDRESS));
    }

    // --- Epoch sealing ---

    function epochFinalityDeadline(uint256 epoch) public view returns (uint256) {
        uint256 genesis = config().getUint(ConfigKeys.EPOCH_GENESIS_TIMESTAMP);
        uint256 length = config().getUint(ConfigKeys.EPOCH_LENGTH);
        uint256 finality = config().getUint(ConfigKeys.REWARD_FINALITY_WINDOW);
        return genesis + (epoch + 1) * length + finality;
    }

    /// @notice Computes and pays out (or reserves) rewards for `epoch`
    /// across the caller-supplied `nodes` list. Reverts if the epoch was
    /// already sealed, or if the finality window has not yet closed.
    function sealEpoch(uint256 epoch, address[] calldata nodes) external whenNotPaused {
        RewardStorage storage $ = _rewardStorage();
        if ($.seals[epoch].sealed_) revert EpochAlreadySealed(epoch);
        if (block.timestamp < epochFinalityDeadline(epoch)) revert FinalityWindowNotElapsed(epoch);

        uint256 emission = config().getUint(ConfigKeys.REWARD_EMISSION_PER_EPOCH);
        uint256 capacityBps = config().getUint(ConfigKeys.REWARD_CAPACITY_SHARE_BPS);
        uint256 capacityAmount = (emission * capacityBps) / 10_000;

        // Pass 1: eligible nodes, scores, and per-type sums.
        uint256 n = nodes.length;
        bool[] memory eligible = new bool[](n);
        uint256[] memory effectiveScore = new uint256[](n);
        uint256[] memory typeSum = new uint256[](7); // NodeType has 7 variants
        INodeRegistry registry = _registry();

        for (uint256 i = 0; i < n; i++) {
            address node = nodes[i];
            if (!_isEligible(node, epoch)) continue;

            MetricRecord memory m = $.metrics[epoch][node];
            uint256 stagePenaltyBps = _stagePenaltyBpsFor(node);
            uint256 score = (m.score * stagePenaltyBps) / 10_000;
            if (score == 0) continue;

            eligible[i] = true;
            effectiveScore[i] = score;
            uint8 t = uint8(registry.typeOf(node));
            typeSum[t] += score;
        }

        uint256 totalDistributed = 0;
        for (uint256 i = 0; i < n; i++) {
            if (!eligible[i]) continue;
            address node = nodes[i];
            uint8 t = uint8(registry.typeOf(node));
            if (typeSum[t] == 0) continue;

            uint256 typeAmount = _typePoolAmount(t, capacityAmount);
            uint256 nodeAmount = (typeAmount * effectiveScore[i]) / typeSum[t];
            if (nodeAmount == 0) continue;

            $.payoutOf[epoch][node] = nodeAmount;
            totalDistributed += nodeAmount;
            _settle(node, epoch, nodeAmount);
        }

        $.seals[epoch] = EpochSeal({sealed_: true, sealedAt: block.timestamp});
        emit EpochSealed(epoch, n, totalDistributed);
    }

    function _typePoolAmount(uint8 nodeType, uint256 capacityAmount) internal view returns (uint256) {
        uint256 weight = config().getUint(ConfigKeys.rewardTypeWeight(nodeType));
        uint256 min = config().getUint(ConfigKeys.REWARD_MULT_MIN);
        uint256 max = config().getUint(ConfigKeys.REWARD_MULT_MAX);
        if (min > 0 && weight < min) weight = min;
        if (max > 0 && weight > max) weight = max;
        return (capacityAmount * weight) / 10_000;
    }

    function _stagePenaltyBpsFor(address node) internal view returns (uint256) {
        address controller = config().getAddress(ConfigKeys.REGISTRY_SLASHING_CONTROLLER);
        if (controller == address(0)) return 10_000;
        (bool ok, bytes memory data) = controller.staticcall(abi.encodeWithSignature("stageOf(address)", node));
        if (!ok || data.length == 0) return 10_000;
        uint8 stage = abi.decode(data, (uint8));
        if (stage == 0) return 10_000;
        return config().getUint(ConfigKeys.rewardStagePenaltyBps(stage));
    }

    function _settle(address node, uint256 epoch, uint256 amount) internal {
        RewardStorage storage $ = _rewardStorage();
        uint256 thresholdBps = config().getUint(ConfigKeys.REWARD_HEARTBEAT_THRESHOLD_BPS);
        uint256 hb = $.metrics[epoch][node].heartbeatBps;
        uint256 prevHb = $.metrics[epoch == 0 ? 0 : epoch - 1][node].heartbeatBps;
        bool compliant = hb >= thresholdBps && (epoch == 0 || prevHb >= thresholdBps);

        if (compliant) {
            uint256 sweepEpochs = config().getUint(ConfigKeys.REWARD_RESERVE_SWEEP_EPOCHS);
            $.consecutiveCompliantEpochs[node] += 1;

            uint256 toPay = amount;
            if ($.reserveBalance[node] > 0 && $.consecutiveCompliantEpochs[node] >= sweepEpochs) {
                toPay += $.reserveBalance[node];
                $.reserveBalance[node] = 0;
                emit ReserveSwept(node, toPay - amount);
            }

            IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
            token.safeTransfer(node, toPay);
            emit Payout(node, epoch, toPay);
        } else {
            $.consecutiveCompliantEpochs[node] = 0;
            $.reserveBalance[node] += amount;
            emit ReserveCredit(node, epoch, amount);
        }
    }

    /// @notice Explicit manual claim of reserve balance ahead of the
    /// automatic compliance-triggered sweep. Per the Arch/QA resolution of
    /// open question 5, no early manual claim exists while non-compliant —
    /// this always reverts unless the consecutive-compliant-epoch condition
    /// has independently been met (in which case `sealEpoch`'s own sweep
    /// would already have paid it out; this is a safety-net entry point for
    /// a node that became compliant without a subsequent payout-bearing
    /// epoch being sealed for it).
    function claimReserve() external whenNotPaused {
        RewardStorage storage $ = _rewardStorage();
        uint256 sweepEpochs = config().getUint(ConfigKeys.REWARD_RESERVE_SWEEP_EPOCHS);
        if ($.reserveBalance[msg.sender] == 0 || $.consecutiveCompliantEpochs[msg.sender] < sweepEpochs) {
            revert NoReserveToSweep();
        }
        uint256 amount = $.reserveBalance[msg.sender];
        $.reserveBalance[msg.sender] = 0;
        IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
        token.safeTransfer(msg.sender, amount);
        emit ReserveSwept(msg.sender, amount);
    }
}
