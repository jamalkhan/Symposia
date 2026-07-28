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
/// @notice Implements FR-4 of issue #52 and reconciles/completes it against
/// issue #53's detailed on-chain epoch reward calculation and auto-payout
/// spec (`Requirements/Network/node-runner-incentives-and-penalties.md`,
/// cross-referenced against `Requirements/Blockchain/tokenomics-mvp.md`
/// §4-6, which is authoritative wherever the two conflict — notably its
/// 15-minute finality window over the incentives doc's illustrative
/// 10-minute figure).
///
/// #53 reconciliation notes (what changed vs #52's scaffold, and why):
/// - **Per-factor scoring (FR2)**: #52 only accepted a single pre-aggregated
///   `score` per node per epoch via `submitMetricReport`. #53 requires
///   per-factor values normalized against the best-in-epoch value for each
///   factor, weighted-summed per the active node-type factor table
///   (tokenomics-mvp.md §6.3). Added `submitFactorMetrics` (Storage's 7
///   factors) plus `_computeScore`, which normalizes/weights at seal time.
///   `submitMetricReport`'s raw-score path is kept as a fallback for
///   node types without a wired factor table yet (FR2.5 pluggability), and
///   because #52's existing test suite exercises it directly.
/// - **Finality window enforcement (FR1.2/FR1.5)**: #52 enforced the window
///   only on `sealEpoch` itself; metric/factor reports submitted after the
///   window (but before someone calls `sealEpoch`) were still accepted and
///   silently included. Both report-submission entrypoints now revert once
///   `block.timestamp >= epochFinalityDeadline(epoch)`.
/// - **Two-epoch heartbeat compliance (FR5.2/FR5.5)**: #52 only special-cased
///   literal epoch index 0 as "no preceding epoch" and used a `>=` 90%
///   comparison. #53 requires (a) a strict `>` 90% comparison (an exact 90%
///   reading must be treated as non-compliant per QA test case 31), and (b)
///   the "no preceding record" exemption to key off whether *that node*
///   has a submitted record for `epoch - 1`, not off epoch index 0 — a node
///   whose first-ever eligible epoch is epoch 5 gets the same one-time
///   exemption a node onboarding at epoch 0 would.
/// - **2-consecutive-epoch reserve sweep (FR6.1)**: already present in #52's
///   scaffold (`consecutiveCompliantEpochs` + `REWARD_RESERVE_SWEEP_EPOCHS`
///   config, defaulted to 2) and verified correct against #53's spec/QA
///   scenarios (streak resets to 0 on any non-compliant epoch; sweep pays
///   the full balance in the same settlement as that epoch's own payout).
///   No functional change was needed here beyond the heartbeat-compliance
///   fix above, which the streak counter itself already consumes correctly.
/// - **Reserve non-expiry/non-compounding (FR5.4)**: already correct in #52
///   (`reserveBalance[node] += amount`, no interest/decay term anywhere) —
///   verified, not changed.
/// - **Emission (FR3.2/FR8.2)**: `REWARD_EMISSION_PER_EPOCH` continues to be
///   read fresh from config every `sealEpoch` call rather than hardcoded or
///   cached, satisfying "not duplicated/hardcoded" for MVP purposes. The
///   full yearly step-down emission schedule contract (tokenomics-mvp.md
///   §5.3/§5.4) is explicitly out of scope per #53's spec ("assumed to
///   exist per #52 or a sibling issue") — this contract defines the
///   dependency shape (a single config-read call site) but does not
///   implement the schedule itself; #57 or a sibling issue should point
///   `REWARD_EMISSION_PER_EPOCH` at a real schedule contract's per-epoch
///   view without touching this contract's call site.
///
/// Deliberately NOT adopted from the Arch pass's four-contract split
/// (EpochController/ScoringEngine/RewardDistributor/ReserveVault): kept as
/// a single contract per the task's explicit reconciliation guidance ("not
/// to build a parallel/duplicate system") — the Arch pass itself notes the
/// two-phase seal/settle split is an interface concern that, at MVP node
/// counts, "will typically execute in one transaction as an implementation
/// convenience" with inline settlement, which is what #52 already built and
/// this issue continues. A follow-up should still isolate `ScoringEngine`/
/// `ReserveVault`-shaped internals into their own contracts before adding
/// non-Storage node types or scaling node counts materially (tracked as a
/// scaling follow-up, consistent with #52's original scaffold note).
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
/// genesis deploy (flagged for #53/#57 follow-up). Multi-type dynamic pool
/// allocation (tokenomics-mvp.md §6.1-6.2) and the 92/8/0 capacity/verifier/
/// Email-IP split (§5.5) remain out of scope per #53's spec — this contract
/// computes scoring/normalization/payout/reserve mechanics for a single
/// already-allocated pool.
///
/// `sealEpoch` takes an explicit, caller-supplied node list rather than
/// iterating the full registry, per the Arch pass's gas/DoS guidance —
/// batch size is controlled by the (permissionless) caller, avoiding an
/// unbounded per-epoch loop while keeping this MVP-simple rather than a
/// fully lazy/pull-based design (tracked as a scaling follow-up). Note for
/// #57: `sealEpoch(uint256 epoch, address[] calldata nodes)` performs both
/// scoring AND payout/reserve settlement inline, synchronously, within the
/// same call — there is no separate deferred `settle()` step in this MVP
/// implementation. Any surplus-routing hook #57 wraps around payout should
/// hook `_settle` (or wrap `sealEpoch` itself), not assume a two-phase
/// entitlement-then-settle call sequence.
contract RewardDistributor is GovernedUpgradeable {
    using SafeERC20 for IERC20;

    /// @notice Number of active Storage factors per tokenomics-mvp.md §6.3:
    /// 0=retrieval speed, 1=uptime, 2=latency/TTFB, 3=I/O throughput,
    /// 4=network bandwidth, 5=available storage, 6=used storage.
    uint8 internal constant STORAGE_FACTOR_COUNT = 7;

    /// @notice Fixed-point scale used for per-factor normalization
    /// (normalized value in [0, NORM_SCALE], NORM_SCALE == 1.0).
    uint256 internal constant NORM_SCALE = 1e18;

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
        // #53 per-factor scoring (FR2): raw per-factor values, whether a
        // node used the factor-report path for a given epoch, and the
        // in-epoch best (max) value observed per factor so far.
        mapping(uint256 => mapping(address => uint256[STORAGE_FACTOR_COUNT])) factorValues;
        mapping(uint256 => mapping(address => bool)) hasFactorReport;
        mapping(uint256 => uint256[STORAGE_FACTOR_COUNT]) factorMax;
    }

    bytes32 private constant REWARD_STORAGE_LOCATION = keccak256("symposia.storage.RewardDistributor");

    event MetricReported(address indexed node, uint256 indexed epoch, uint256 score, uint256 heartbeatBps);
    event FactorMetricsReported(address indexed node, uint256 indexed epoch, uint256 heartbeatBps);
    event EpochSealed(uint256 indexed epoch, uint256 nodeCount, uint256 totalCapacityDistributed);
    event Payout(address indexed node, uint256 indexed epoch, uint256 amount);
    event ReserveCredit(address indexed node, uint256 indexed epoch, uint256 amount);
    event ReserveSwept(address indexed node, uint256 amount);

    error EpochAlreadySealed(uint256 epoch);
    error FinalityWindowNotElapsed(uint256 epoch);
    error FinalityWindowElapsed(uint256 epoch);
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
        _checkReportWindow(epoch);

        bytes32 digest = MessageHashUtils.toEthSignedMessageHash(
            keccak256(abi.encode(address(this), node, epoch, score, heartbeatBps))
        );
        address signer = ECDSA.recover(digest, signature);
        if (signer != config().getAddress(ConfigKeys.REWARD_METRIC_SIGNER)) revert InvalidSignature();

        _rewardStorage().metrics[epoch][node] = MetricRecord({score: score, heartbeatBps: heartbeatBps, submitted: true});
        emit MetricReported(node, epoch, score, heartbeatBps);
    }

    /// @notice Ingests a signed per-node, per-epoch **per-factor** metric
    /// report (#53 FR2) for the Storage factor table (tokenomics-mvp.md
    /// §6.3). `values` are raw (non-normalized) per-factor readings in
    /// whatever unit the off-chain aggregation pipeline reports (e.g. bytes/
    /// sec for retrieval speed) — normalization against the in-epoch best
    /// performer happens at `sealEpoch` time (FR2.2), not here, since the
    /// best-in-epoch value for a factor is not known until all of that
    /// epoch's reports are in.
    ///
    /// Nodes using this entrypoint have their `_computeScore` result derived
    /// from the weighted normalized factor sum rather than a flat `score`;
    /// `submitMetricReport`'s raw-score path remains available for node
    /// types without a wired factor table (FR2.5 pluggability) and is left
    /// untouched for #52's existing test coverage.
    function submitFactorMetrics(
        address node,
        uint256 epoch,
        uint256[STORAGE_FACTOR_COUNT] calldata values,
        uint256 heartbeatBps,
        bytes calldata signature
    ) external whenNotPaused {
        _checkReportWindow(epoch);

        bytes32 digest = MessageHashUtils.toEthSignedMessageHash(
            keccak256(abi.encode(address(this), node, epoch, values, heartbeatBps))
        );
        address signer = ECDSA.recover(digest, signature);
        if (signer != config().getAddress(ConfigKeys.REWARD_METRIC_SIGNER)) revert InvalidSignature();

        RewardStorage storage $ = _rewardStorage();
        for (uint8 i = 0; i < STORAGE_FACTOR_COUNT; i++) {
            $.factorValues[epoch][node][i] = values[i];
            if (values[i] > $.factorMax[epoch][i]) {
                $.factorMax[epoch][i] = values[i];
            }
        }
        $.hasFactorReport[epoch][node] = true;
        // Reuses MetricRecord for eligibility (`submitted`) and heartbeat
        // compliance bookkeeping; `score` is unused on this path (ignored by
        // `_computeScore` whenever `hasFactorReport` is set).
        $.metrics[epoch][node] = MetricRecord({score: 0, heartbeatBps: heartbeatBps, submitted: true});
        emit FactorMetricsReported(node, epoch, heartbeatBps);
    }

    /// @notice Rejects metric/factor reports once the epoch's 15-minute (MVP
    /// default, governance-configurable) finality window has elapsed, or the
    /// epoch has already been sealed (#53 FR1.2/FR1.5) — late reports must
    /// not be silently incorporated into a seal that reads current storage.
    function _checkReportWindow(uint256 epoch) internal view {
        if (_rewardStorage().seals[epoch].sealed_) revert EpochAlreadySealed(epoch);
        if (block.timestamp >= epochFinalityDeadline(epoch)) revert FinalityWindowElapsed(epoch);
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

            uint256 rawScore = _computeScore(epoch, node);
            uint256 stagePenaltyBps = _stagePenaltyBpsFor(node);
            uint256 score = (rawScore * stagePenaltyBps) / 10_000;
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

    /// @notice #53 FR2.2/FR2.3: per-node epoch score. If the node used the
    /// per-factor report path for this epoch, this is the weighted sum of
    /// its normalized (against the in-epoch best-in-factor value) per-factor
    /// scores using the node type's active factor-weight table; a factor
    /// with no reported in-epoch max (i.e. nobody reported it) contributes
    /// 0 rather than reverting. A node with no reported value for a factor
    /// scores 0 for that factor (FR2.2) but is not excluded from the epoch
    /// on that basis alone. Otherwise (no factor report submitted for this
    /// epoch) falls back to the flat `score` from `submitMetricReport`, for
    /// node types without a wired factor table (FR2.5).
    function _computeScore(uint256 epoch, address node) internal view returns (uint256) {
        RewardStorage storage $ = _rewardStorage();
        if (!$.hasFactorReport[epoch][node]) {
            return $.metrics[epoch][node].score;
        }

        uint8 nodeType = uint8(_registry().typeOf(node));
        uint256 weighted;
        for (uint8 i = 0; i < STORAGE_FACTOR_COUNT; i++) {
            uint256 maxVal = $.factorMax[epoch][i];
            if (maxVal == 0) continue;
            uint256 normalized = ($.factorValues[epoch][node][i] * NORM_SCALE) / maxVal;
            uint256 weightBps = config().getUint(ConfigKeys.rewardFactorWeightBps(nodeType, i));
            weighted += (normalized * weightBps) / 10_000;
        }
        return weighted;
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

        // #53 FR5.2: strict ">90%" (not ">="), evaluated across the current
        // epoch AND the immediately preceding epoch. FR5.5: a node with no
        // preceding-epoch record for *itself* (not merely epoch index 0) is
        // judged on the current epoch's compliance alone for this one
        // payout — this correctly exempts a node whose first-ever eligible
        // epoch is e.g. epoch 5, not just epoch 0.
        bool hasPrevRecord = epoch > 0 && $.metrics[epoch - 1][node].submitted;
        bool ownEpochCompliant = hb > thresholdBps;
        bool autoPayEligible =
            ownEpochCompliant && (!hasPrevRecord || $.metrics[epoch - 1][node].heartbeatBps > thresholdBps);

        // #53 FR6.1: the sweep streak counts each epoch's OWN >90% reading
        // consecutively (reset to 0 on any non-compliant epoch), which is a
        // distinct signal from `autoPayEligible` above (a two-epoch lookback
        // gate on *this* epoch's payout). Conflating the two under-counts
        // the streak: e.g. epoch0 non-compliant, epoch1 compliant, epoch2
        // compliant should register a streak of 2 by epoch2 (triggering the
        // sweep) even though epoch1 itself wasn't auto-pay-eligible (its own
        // preceding epoch, epoch0, was non-compliant).
        uint256 sweepEpochs = config().getUint(ConfigKeys.REWARD_RESERVE_SWEEP_EPOCHS);
        if (ownEpochCompliant) {
            $.consecutiveCompliantEpochs[node] += 1;
        } else {
            $.consecutiveCompliantEpochs[node] = 0;
        }

        if (autoPayEligible) {
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
            // A non-auto-pay epoch can still complete the sweep streak (per
            // FR6.1's example: epoch X's own reading pushes the streak to
            // the threshold even though epoch X's *own* payout is held,
            // because epoch X's preceding epoch failed the two-epoch test).
            // Sweep independently of this epoch's own payout routing.
            if ($.reserveBalance[node] > 0 && $.consecutiveCompliantEpochs[node] >= sweepEpochs) {
                uint256 swept = $.reserveBalance[node];
                $.reserveBalance[node] = 0;
                IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
                token.safeTransfer(node, swept);
                emit ReserveSwept(node, swept);
            }
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
