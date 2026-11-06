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
import {IFoundationRewardHook} from "./interfaces/IFoundationRewardHook.sol";
import {INetworkRewardsEmission} from "./interfaces/INetworkRewardsEmission.sol";
import {IReliabilityRegistry} from "./interfaces/IReliabilityRegistry.sol";
import {IEpochController} from "./interfaces/IEpochController.sol";
import {BonusNormalization} from "./libraries/BonusNormalization.sol";
import {FactorScoring} from "./libraries/FactorScoring.sol";
import {ITelemetryRecords, IScoringWeights} from "./interfaces/ITelemetryRecords.sol";
import {TelemetryCheckpointRegistry} from "./TelemetryCheckpointRegistry.sol";

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

    /// @notice Why a node got (or didn't get) its epoch payout (#80 FR10).
    enum SettlementReason {
        None,
        Paid, // positive payout, auto-paid or held per the heartbeat gate (see Payout/ReserveCredit)
        ExcludedType, // EmailIP (never), Compute (fee-funded) and services without defined economics
        Ineligible, // failed region-verification, stake or metric-report gate, or zero score
        ZeroStagePenalty, // Stage 3/4: multiplier 0
        ZeroAmount // eligible but rounded to 0 (or reliability 0)
    }

    struct SettlementRecord {
        SettlementReason reason;
        uint16 reliabilityBps;
        uint16 stagePenaltyBps;
        bool reliabilityBonus;
        uint128 amount;
        uint64 score; // #76: epoch score (WAD on the factor path), appended within the same slot
    }

    /// @notice Where an epoch's metrics come from at settlement (#76/#158).
    enum EpochSource {
        Pending, // telemetry records not final yet and the fallback wait hasn't passed
        Telemetry, // finalized committee records (storage nodes), signed reports for other types
        Signed // no telemetry registry, or records never finalized within REWARD_RECORDS_MAX_WAIT
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
        // #80: per-(epoch, node) settlement decision, appended for upgrade safety.
        mapping(uint256 => mapping(address => SettlementRecord)) settlements;
        // Review fixes: reporters per epoch (sealEpoch must cover all of them),
        // monotonic seal watermark (epoch + 1 of the last seal; 0 = none yet),
        // and capacity SYM received but not assigned to any node.
        mapping(uint256 => uint256) reporterCount;
        uint256 nextSealableEpoch;
        uint256 undistributed;
        // #76: signed per-type factor reports (length = ScoringWeights.factorCount(type)).
        mapping(uint256 => mapping(address => uint256[])) factorRaw;
        // #159: distinct reporting nodes per epoch (reporterCount counts reward accounts).
        mapping(uint256 => mapping(address => bool)) nodeReported;
        mapping(uint256 => uint256) reportingNodeCount;
        // Review fix: per-seal duplicate guard, so no placeholder settlement is left behind.
        mapping(uint256 => mapping(address => bool)) sealListed;
    }

    bytes32 private constant REWARD_STORAGE_LOCATION = keccak256("symposia.storage.RewardDistributor");

    event MetricReported(address indexed node, uint256 indexed epoch, uint256 score, uint256 heartbeatBps);
    event FactorMetricsReported(address indexed node, uint256 indexed epoch, uint256 heartbeatBps);
    event EpochSealed(uint256 indexed epoch, uint256 nodeCount, uint256 totalCapacityDistributed);
    event UndistributedRecorded(uint256 indexed epoch, uint256 amount, uint256 total);
    event UndistributedSwept(address indexed to, uint256 amount);
    /// @notice One per node per sealed epoch: enough to reconstruct the
    /// payout (`typePool * score / typeScoreSum * reliability * stage`, then
    /// the #79 bonus normalization) for #87 alerting and audit.
    /// @notice #76 per-factor transparency: raw inputs and the resulting score.
    event NodeScored(address indexed node, uint256 indexed epoch, uint8 nodeType, uint256[] raw, uint256 score);
    event NodeSettled(
        address indexed node,
        uint256 indexed epoch,
        SettlementReason indexed reason,
        uint256 score,
        uint256 typeScoreSum,
        uint256 typePool,
        uint256 reliabilityBps,
        uint256 stagePenaltyBps,
        bool reliabilityBonus,
        uint256 amount
    );
    /// @notice #159: one per settled Node Service. `account` is the service's reward
    /// account (the node itself for its primary service); `paid` is what was transferred
    /// to the node now (including any released reserve) and `held` what went to reserve.
    event ServiceSettled(
        address indexed node,
        INodeRegistry.NodeType indexed service,
        uint256 indexed epoch,
        SettlementReason reason,
        address account,
        uint256 amount,
        uint256 paid,
        uint256 held
    );
    event ServiceMetricsReported(address indexed node, INodeRegistry.NodeType indexed service, uint256 indexed epoch);
    event ServiceReserveClaimed(address indexed node, INodeRegistry.NodeType indexed service, uint256 amount);
    event Payout(address indexed node, uint256 indexed epoch, uint256 amount);
    event ReserveCredit(address indexed node, uint256 indexed epoch, uint256 amount);
    event ReserveSwept(address indexed node, uint256 amount);
    /// @notice Issue #57 §6.4: emitted whenever the (optional)
    /// `FOUNDATION_REWARD_HOOK` routes a foundation node's epoch surplus to
    /// the ecosystem reserve, so the transfer is independently auditable
    /// on-chain rather than only claimed in documentation.
    event FoundationSurplusRouted(address indexed node, uint256 indexed epoch, uint256 amount, address indexed reserveRecipient);

    error EpochAlreadySealed(uint256 epoch);
    error FinalityWindowNotElapsed(uint256 epoch);
    error FinalityWindowElapsed(uint256 epoch);
    error InvalidSignature();
    error NoReserveToSweep();
    error DuplicateNode(address node);
    error IncompleteNodeSet(uint256 epoch, uint256 covered, uint256 reporters);
    error EpochSealOutOfOrder(uint256 epoch, uint256 nextSealable);
    error SweepExceedsUndistributed(uint256 amount, uint256 undistributed);
    error TelemetryRecordsPending(uint256 epoch);
    error FactorCountMismatch(uint256 given, uint256 expected);
    error PoolsOverAllocated(uint256 assigned, uint256 available);

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
        _checkSigner(keccak256(abi.encode(address(this), node, epoch, score, heartbeatBps)), signature);
        _storeMetric(epoch, node, score, heartbeatBps);
        _countReportingNode(epoch, node);
        emit MetricReported(node, epoch, score, heartbeatBps);
    }

    /// @notice #159: single-score report for one of `node`'s Node Services. The service
    /// is part of the signed digest, so a report can't be replayed for another service.
    function submitServiceMetricReport(
        address node,
        INodeRegistry.NodeType service,
        uint256 epoch,
        uint256 score,
        uint256 heartbeatBps,
        bytes calldata signature
    ) external whenNotPaused {
        _checkReportWindow(epoch);
        _checkSigner(keccak256(abi.encode(address(this), node, uint8(service), epoch, score, heartbeatBps)), signature);
        _storeMetric(epoch, accountOf(node, service), score, heartbeatBps);
        _countReportingNode(epoch, node);
        emit MetricReported(node, epoch, score, heartbeatBps);
        emit ServiceMetricsReported(node, service, epoch);
    }

    function _storeMetric(uint256 epoch, address account, uint256 score, uint256 heartbeatBps) private {
        _countReporter(epoch, account);
        _rewardStorage().metrics[epoch][account] = MetricRecord({score: score, heartbeatBps: heartbeatBps, submitted: true});
    }

    function _checkSigner(bytes32 hash, bytes calldata signature) private view {
        address signer = ECDSA.recover(MessageHashUtils.toEthSignedMessageHash(hash), signature);
        if (signer != config().getAddress(ConfigKeys.REWARD_METRIC_SIGNER)) revert InvalidSignature();
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
    /// @notice Signed per-factor report for one node (#53 FR2, generalized by #76):
    /// `values` follows ScoringWeights' factor order for the node's type. Storage
    /// nodes are scored from telemetry records instead whenever those finalize.
    function submitFactorMetrics(
        address node,
        uint256 epoch,
        uint256[] calldata values,
        uint256 heartbeatBps,
        bytes calldata signature
    ) external whenNotPaused {
        _checkReportWindow(epoch);
        _checkFactorCount(uint8(_registry().typeOf(node)), values.length);
        _checkSigner(keccak256(abi.encode(address(this), node, epoch, values, heartbeatBps)), signature);
        _storeFactors(epoch, node, values, heartbeatBps);
        _countReportingNode(epoch, node);
        emit FactorMetricsReported(node, epoch, heartbeatBps);
    }

    /// @notice #159: per-factor report for one of `node`'s Node Services (service in the digest).
    function submitServiceFactorMetrics(
        address node,
        INodeRegistry.NodeType service,
        uint256 epoch,
        uint256[] calldata values,
        uint256 heartbeatBps,
        bytes calldata signature
    ) external whenNotPaused {
        _checkReportWindow(epoch);
        _checkFactorCount(uint8(service), values.length);
        _checkSigner(keccak256(abi.encode(address(this), node, uint8(service), epoch, values, heartbeatBps)), signature);
        _storeFactors(epoch, accountOf(node, service), values, heartbeatBps);
        _countReportingNode(epoch, node);
        emit FactorMetricsReported(node, epoch, heartbeatBps);
        emit ServiceMetricsReported(node, service, epoch);
    }

    function _checkFactorCount(uint8 service, uint256 given) private view {
        uint256 expected = _weights().factorCount(service);
        if (given != expected) revert FactorCountMismatch(given, expected);
    }

    function _storeFactors(uint256 epoch, address account, uint256[] calldata values, uint256 heartbeatBps) private {
        RewardStorage storage $ = _rewardStorage();
        $.factorRaw[epoch][account] = values;
        _countReporter(epoch, account);
        $.hasFactorReport[epoch][account] = true;
        // `score` is unused on the factor path; MetricRecord carries eligibility and heartbeat.
        $.metrics[epoch][account] = MetricRecord({score: 0, heartbeatBps: heartbeatBps, submitted: true});
    }

    /// @notice #159: the reward account of `node`'s `service`. The primary (first
    /// registered) service uses the node address itself, so single-service nodes are
    /// unchanged; every other service gets a deterministic derived address that has no
    /// key and never receives tokens (payouts always go to `node`). Metric reports,
    /// reserves, streaks, payouts, settlements and ReliabilityRegistry state for that
    /// service are keyed by this account.
    function accountOf(address node, INodeRegistry.NodeType service) public view returns (address) {
        INodeRegistry registry = _registry();
        if (address(registry) == address(0) || !registry.hasPrimaryService(node) || registry.typeOf(node) == service) {
            return node;
        }
        return address(uint160(uint256(keccak256(abi.encode("symposia.service-account", node, uint8(service))))));
    }

    /// @notice Rejects metric/factor reports for an epoch that hasn't started,
    /// whose finality window (EpochController, #72) has elapsed, or that has
    /// already been sealed (#53 FR1.2/FR1.5). Late reports are rejected, never
    /// rolled into the next epoch.
    function _checkReportWindow(uint256 epoch) internal view {
        if (_rewardStorage().seals[epoch].sealed_) revert EpochAlreadySealed(epoch);
        if (!_epochs().acceptsReports(epoch)) revert FinalityWindowElapsed(epoch);
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

    /// @notice The gross (pre-foundation-surplus-routing) epoch reward
    /// computed by the reward engine for `node`, i.e. before any #57
    /// operator/reserve split is applied by `_payNode`. Exposed as a public
    /// getter so surplus routing is independently auditable against the
    /// underlying calculation (AC6), not just against the operator-visible
    /// transfer amount.
    function payoutOf(uint256 epoch, address node) external view returns (uint256) {
        return _rewardStorage().payoutOf[epoch][node];
    }

    // --- Eligibility ---

    function _isEligible(address node, INodeRegistry.NodeType service, address account, uint256 epoch)
        internal
        view
        returns (bool)
    {
        INodeRegistry registry = _registry();
        if (address(registry) == address(0)) return false;

        MetricRecord memory m = _rewardStorage().metrics[epoch][account];
        if (!m.submitted) return false;

        if (registry.serviceStatusOf(node, service) != INodeRegistry.NodeStatus.Active) return false;
        // Approximates "passed region verification before epoch start" (FR-4
        // item 6): the node's most recent recorded verification must be at
        // or before the epoch being sealed. `Active` status already implies
        // at least one passing verification, so this only needs to check
        // recency, not presence.
        if (registry.serviceLastVerifiedEpochOf(node, service) > epoch) return false;

        uint256 required = registry.minStakeFor(service, 0);
        // NOTE: capacity is not re-read here (registry does not expose it via
        // INodeRegistry's narrow interface); this checks stake against the
        // node-type base minimum as a floor. A tighter per-capacity check is
        // available to callers via the full StakingNodeRegistry ABI and is
        // left as a documented follow-up for #53/#57 to tighten if needed.
        if (registry.serviceStakeOf(node, service) < required) return false;

        return true;
    }

    function _registry() internal view returns (INodeRegistry) {
        return INodeRegistry(config().getAddress(ConfigKeys.REGISTRY_ADDRESS));
    }

    // --- Epoch sealing ---

    /// @notice IEpochClock (#73): timestamp at which `epoch` ends. Forwards to
    /// the EpochController (#72), which owns all epoch math.
    function epochEndTimestamp(uint256 epoch) public view returns (uint256) {
        return _epochs().epochEnd(epoch);
    }

    /// @notice Last second (inclusive) at which reports for `epoch` are accepted.
    function epochFinalityDeadline(uint256 epoch) public view returns (uint256) {
        return _epochs().finalityDeadline(epoch);
    }

    function _epochs() internal view returns (IEpochController) {
        return IEpochController(config().getAddress(ConfigKeys.EPOCH_CONTROLLER));
    }

    function _telemetry() internal view returns (ITelemetryRecords) {
        return ITelemetryRecords(config().getAddress(ConfigKeys.REWARD_TELEMETRY_REGISTRY));
    }

    function _weights() internal view returns (IScoringWeights) {
        return IScoringWeights(config().getAddress(ConfigKeys.REWARD_SCORING_WEIGHTS));
    }

    uint256 internal constant DEFAULT_RECORDS_MAX_WAIT = 24 hours;

    /// @notice Where `epoch`'s metrics will come from if it's settled now (#76/#158):
    /// finalized telemetry records; or, with no telemetry registry or once
    /// `telemetryDeadline + REWARD_RECORDS_MAX_WAIT` has passed without them, signed
    /// reports, so a committee outage can't stall payouts forever.
    function epochSource(uint256 epoch) public view returns (EpochSource) {
        ITelemetryRecords t = _telemetry();
        if (address(t) == address(0)) return EpochSource.Signed;
        if (t.recordsFinalized(epoch)) return EpochSource.Telemetry;
        uint256 wait = config().getUint(ConfigKeys.REWARD_RECORDS_MAX_WAIT);
        if (block.timestamp > t.telemetryDeadline(epoch) + (wait == 0 ? DEFAULT_RECORDS_MAX_WAIT : wait)) {
            return EpochSource.Signed;
        }
        return EpochSource.Pending;
    }

    /// @notice Whether `sealEpoch(epoch, …)` can succeed now (given a complete node list).
    function canSeal(uint256 epoch) external view returns (bool) {
        RewardStorage storage $ = _rewardStorage();
        return !$.seals[epoch].sealed_ && epoch == $.nextSealableEpoch && _epochs().isSealed(epoch)
            && epochSource(epoch) != EpochSource.Pending;
    }

    /// @notice Number of Node Service types (`INodeRegistry.NodeType`, #159).
    uint8 internal constant SERVICE_TYPE_COUNT = 11;

    /// @notice Capacity Node Services that share the 92% emission (tokenomics-mvp.md
    /// §5.5): Storage, OLTP, Analytics, Consensus, Serverless (ordinals 0-4), and the
    /// Blob Gateway (8, #160 strawman), each with its own type pool.
    /// Verifier (8%, #81), EmailIP (0%, never) and Compute (fee-funded, #90) are
    /// excluded from this flow, as are services whose economics aren't defined yet.
    function _isCapacityService(uint8 t) internal pure returns (bool) {
        return t < 5 || t == uint8(INodeRegistry.NodeType.BlobGateway);
    }

    uint8 internal constant VERIFIER = uint8(INodeRegistry.NodeType.Verifier);
    uint8 internal constant EMAIL_IP = uint8(INodeRegistry.NodeType.EmailIP);

    /// @notice Services paid from emission: the capacity services (92%) and Verifier
    /// (8%, #81). Email IP Address is hardcoded out: no config can route SYM to it.
    function _isPaidService(uint8 t) internal pure returns (bool) {
        if (t == EMAIL_IP) return false;
        return _isCapacityService(t) || t == VERIFIER;
    }

    /// @dev Per-node working state for one `sealEpoch` call.
    struct NodeCalc {
        address node;
        uint8 nodeType;
        uint256 score;
        uint256 reliabilityBps;
        uint256 stagePenaltyBps;
        bool bonus;
        uint256 amount;
        SettlementReason reason;
        uint256[] raw; // #76 factor values; empty = legacy single-score report
        address account; // #159 reward account of (node, nodeType)
    }

    /// @notice Computes and pays out (or reserves) rewards for `epoch`
    /// across the caller-supplied `nodes` list. Reverts if the epoch was
    /// already sealed, the finality window hasn't closed, a node appears
    /// twice, `nodes` omits any node that reported metrics for the epoch
    /// (so a caller can't shrink the score denominator and capture a pool),
    /// or `epoch` is not exactly the next unsealed epoch. Seals are strictly
    /// sequential from epoch 0, so reliability finalization is always in
    /// order and no epoch (or its emission or degradations) can be skipped.
    /// An epoch with no reporters is sealed with an empty list.
    ///
    /// Payout (#80 FR1): `typePool * score / typeScoreSum * reliability *
    /// stagePenalty`, then the #79 reliability bonus is peer-normalized
    /// within the type (BonusNormalization). The denominator is the raw
    /// score sum of eligible nodes. Shares cut by reliability or stage
    /// penalties (Stage 3/4 = 0) are forfeited, not redistributed to peers,
    /// so per-type payouts are always `<= typePool`. Forfeited and rounding
    /// amounts stay in this contract. Verifiers (#81) are paid the same way from the
    /// 8% verifier slice, scored on uptime, coverage and accuracy.
    function sealEpoch(uint256 epoch, address[] calldata nodes) external whenNotPaused {
        RewardStorage storage $ = _rewardStorage();
        if ($.seals[epoch].sealed_) revert EpochAlreadySealed(epoch);
        if (!_epochs().isSealed(epoch)) revert FinalityWindowNotElapsed(epoch);
        if (epoch != $.nextSealableEpoch) revert EpochSealOutOfOrder(epoch, $.nextSealableEpoch);
        EpochSource source = epochSource(epoch);
        if (source == EpochSource.Pending) revert TelemetryRecordsPending(epoch);
        $.nextSealableEpoch = epoch + 1;
        // Latch before any external call (emission mint, reliability finalize, payouts).
        $.seals[epoch] = EpochSeal({sealed_: true, sealedAt: block.timestamp});
        _epochs().trySeal(epoch); // records the controller seal and its event; no-op if already recorded

        uint256 emission = _epochEmission(epoch);
        uint256 capacityAmount = (emission * config().getUint(ConfigKeys.REWARD_CAPACITY_SHARE_BPS)) / 10_000;
        // #81: the verifier slice (tokenomics-mvp.md §5.5), floored; remainders and an
        // epoch with no eligible verifier leave it in `undistributed`.
        uint256 verifierAmount = (emission * config().getUint(ConfigKeys.REWARD_VERIFIER_SHARE_BPS)) / 10_000;

        NodeCalc[] memory calc = _scoreNodes(epoch, nodes, source == EpochSource.Telemetry);

        uint256[] memory typeSum = new uint256[](SERVICE_TYPE_COUNT);
        uint256[] memory typePool = new uint256[](SERVICE_TYPE_COUNT);
        for (uint256 i = 0; i < calc.length; i++) {
            if (calc[i].reason == SettlementReason.None) typeSum[calc[i].nodeType] += calc[i].score;
        }
        for (uint8 t = 0; t < SERVICE_TYPE_COUNT; t++) {
            if (typeSum[t] == 0 || !_isPaidService(t)) continue;
            typePool[t] = t == VERIFIER ? verifierAmount : _typePoolAmount(t, capacityAmount);
            _distributeType(calc, t, typeSum[t], typePool[t]);
        }

        // Review fix: config whose shares or type weights sum past 100% fails here with
        // a clear error instead of underflowing `emission - totalDistributed` below.
        uint256 assigned;
        for (uint8 t = 0; t < SERVICE_TYPE_COUNT; t++) assigned += typePool[t];
        if (assigned > emission) revert PoolsOverAllocated(assigned, emission);

        uint256 totalDistributed = 0;
        for (uint256 i = 0; i < calc.length; i++) {
            NodeCalc memory c = calc[i];
            if (c.reason == SettlementReason.None) {
                // Precedence for #87: a Stage 3/4 node is ZeroStagePenalty even if
                // its reliability is also 0; ZeroAmount covers every other zero.
                c.reason = c.stagePenaltyBps == 0
                    ? SettlementReason.ZeroStagePenalty
                    : (c.amount == 0 ? SettlementReason.ZeroAmount : SettlementReason.Paid);
            }
            $.settlements[epoch][c.account] = SettlementRecord({
                reason: c.reason,
                reliabilityBps: uint16(c.reliabilityBps),
                stagePenaltyBps: uint16(c.stagePenaltyBps),
                reliabilityBonus: c.bonus,
                amount: uint128(c.amount),
                score: uint64(c.score)
            });
            bool pooled = c.nodeType < SERVICE_TYPE_COUNT;
            emit NodeSettled(
                c.account,
                epoch,
                c.reason,
                c.score,
                pooled ? typeSum[c.nodeType] : 0,
                pooled ? typePool[c.nodeType] : 0,
                c.reliabilityBps,
                c.stagePenaltyBps,
                c.bonus,
                c.amount
            );
            uint256 paid;
            uint256 held;
            if (c.reason == SettlementReason.Paid) {
                $.payoutOf[epoch][c.account] = c.amount;
                totalDistributed += c.amount;
                (paid, held) = _settle(c.account, c.node, epoch, c.amount);
            }
            if (c.nodeType < SERVICE_TYPE_COUNT) {
                emit ServiceSettled(
                    c.node, INodeRegistry.NodeType(c.nodeType), epoch, c.reason, c.account, c.amount, paid, held
                );
            }
        }

        // Everything received for the epoch but not assigned to a node (forfeits,
        // ineligible and empty pools, an epoch without eligible verifiers, rounding).
        // Held reserve credits count as assigned: they are owed to the node.
        uint256 leftover = emission - totalDistributed;
        if (leftover > 0) {
            $.undistributed += leftover;
            emit UndistributedRecorded(epoch, leftover, $.undistributed);
        }
        emit EpochSealed(epoch, nodes.length, totalDistributed);
    }

    /// @dev Pass 1: dedupe, type exclusion, eligibility gates, raw score,
    /// stage multiplier, and reliability finalization (#79) for every
    /// capacity node that reported this epoch.
    /// Metric source: still this contract's own `REWARD_METRIC_SIGNER`-signed
    /// reports. #75's TelemetryCheckpointRegistry.getEpochMetricRecord is
    /// the intended source once #76 (scoring engine) reads from it. Until
    /// then, the signer is expected to sign from those aggregated records.
    function _scoreNodes(uint256 epoch, address[] calldata nodes, bool telemetry)
        internal
        returns (NodeCalc[] memory calc)
    {
        RewardStorage storage $ = _rewardStorage();
        INodeRegistry registry = _registry();
        IReliabilityRegistry rel = IReliabilityRegistry(config().getAddress(ConfigKeys.REWARD_RELIABILITY_REGISTRY));
        // #159: every listed node expands into one unit per Node Service.
        INodeRegistry.NodeType[][] memory services = new INodeRegistry.NodeType[][](nodes.length);
        uint256 units;
        for (uint256 i = 0; i < nodes.length; i++) {
            services[i] = _settlementServices(registry, nodes[i]);
            units += services[i].length;
        }
        calc = new NodeCalc[](units);
        uint256 coveredReports;
        uint256 coveredRecords;
        uint256 u;

        for (uint256 i = 0; i < nodes.length; i++) {
            address node = nodes[i];
            if ($.sealListed[epoch][node]) revert DuplicateNode(node);
            $.sealListed[epoch][node] = true;

            TelemetryCheckpointRegistry.AggregatedMetricRecord memory r;
            if (telemetry) {
                r = _telemetry().getEpochMetricRecord(node, epoch);
                if (r.exists) coveredRecords++;
            }
            for (uint256 k = 0; k < services[i].length; k++) {
                NodeCalc memory c = calc[u++];
                if (_scoreUnit(epoch, node, services[i][k], registry, rel, r, c)) coveredReports++;
            }
        }
        if (coveredReports != $.reporterCount[epoch]) {
            revert IncompleteNodeSet(epoch, coveredReports, $.reporterCount[epoch]);
        }
        if (telemetry && coveredRecords != _telemetry().recordCount(epoch)) {
            revert IncompleteNodeSet(epoch, coveredRecords, _telemetry().recordCount(epoch));
        }

        _scoreFactors(epoch, calc);
    }

    /// @dev The services settled for `node`: its registered services, or a single
    /// legacy unit (primary type, account = node) for a node with none. Without a
    /// registry, one unit that is always excluded.
    function _settlementServices(INodeRegistry registry, address node)
        private
        view
        returns (INodeRegistry.NodeType[] memory list)
    {
        if (address(registry) != address(0)) {
            list = registry.servicesOf(node);
            if (list.length > 0) return list;
        }
        list = new INodeRegistry.NodeType[](1);
        if (address(registry) != address(0)) list[0] = registry.typeOf(node);
    }

    /// @dev Pass 1 for one (node, service) unit: account, type exclusion, telemetry
    /// ingestion, reliability finalization (#79), eligibility, raw score and stage
    /// multiplier. Returns whether the unit's account had a metric report.
    function _scoreUnit(
        uint256 epoch,
        address node,
        INodeRegistry.NodeType service,
        INodeRegistry registry,
        IReliabilityRegistry rel,
        TelemetryCheckpointRegistry.AggregatedMetricRecord memory r,
        NodeCalc memory c
    ) private returns (bool reported) {
        RewardStorage storage $ = _rewardStorage();
        c.node = node;
        c.reliabilityBps = 10_000;
        c.stagePenaltyBps = 10_000;
        c.account = accountOf(node, service);
        c.nodeType = address(registry) == address(0) ? type(uint8).max : uint8(service);
        reported = $.metrics[epoch][c.account].submitted;
        // Telemetry mode: the Storage service is judged on its committee record (#76).
        bool recordIneligible;
        if (r.exists && service == INodeRegistry.NodeType.Storage && address(registry) != address(0)) {
            _ingestRecord(epoch, c, r);
            recordIneligible = !r.eligible;
        }
        if (!_isPaidService(c.nodeType)) {
            c.reason = SettlementReason.ExcludedType;
            return reported;
        }

        MetricRecord memory m = $.metrics[epoch][c.account];
        if (address(rel) != address(0) && m.submitted) {
            (c.reliabilityBps, c.bonus) = _finalizeReliability(rel, c.account, epoch, m.heartbeatBps);
        }

        if (recordIneligible || !_isEligible(node, service, c.account, epoch)) {
            c.reason = SettlementReason.Ineligible;
            return reported;
        }
        if (c.raw.length == 0) {
            if ($.hasFactorReport[epoch][c.account]) c.raw = $.factorRaw[epoch][c.account];
            else c.score = m.score; // legacy single-score report (#53)
        }
        c.stagePenaltyBps = _stagePenaltyBpsFor(node);
    }

    /// @dev A Storage service with a finalized record is judged on it (#76). Its
    /// eligibility and heartbeat compliance replace any signed report, so the reserve
    /// gate and reliability read measured data.
    function _ingestRecord(
        uint256 epoch,
        NodeCalc memory c,
        TelemetryCheckpointRegistry.AggregatedMetricRecord memory r
    ) private {
        _rewardStorage().metrics[epoch][c.account] =
            MetricRecord({score: 0, heartbeatBps: r.heartbeatComplianceBps, submitted: true}); // eligibility checked by the caller
        // Verifier-attested latency/retrieval when a verifier measured them, else self-reported.
        bool attested = r.attestationCount > 0;
        c.raw = new uint256[](7);
        c.raw[0] = attested && r.verifierAttested.retrievalMs != 0 ? r.verifierAttested.retrievalMs : r.selfReported.retrievalMs;
        c.raw[1] = r.heartbeatComplianceBps;
        c.raw[2] = attested && r.verifierAttested.latencyMs != 0 ? r.verifierAttested.latencyMs : r.selfReported.latencyMs;
        c.raw[3] = uint256(r.selfReported.readThroughputKBps) + r.selfReported.writeThroughputKBps;
        c.raw[4] = uint256(r.selfReported.bandwidthInKbps) + r.selfReported.bandwidthOutKbps;
        c.raw[5] = r.availableBytes;
        c.raw[6] = r.usedBytes;
    }

    /// @dev #76 two-pass scoring: per type, find the best value of each factor across
    /// the eligible factor-path nodes, then score each node against it with the weight
    /// table in force for this epoch. A zero score makes the node ineligible.
    function _scoreFactors(uint256 epoch, NodeCalc[] memory calc) private {
        for (uint8 t = 0; t < SERVICE_TYPE_COUNT; t++) {
            uint256 count;
            for (uint256 i; i < calc.length; i++) {
                if (calc[i].nodeType == t && calc[i].reason == SettlementReason.None && calc[i].raw.length > 0) count++;
            }
            if (count > 0) {
                (uint16[] memory weights, bool[] memory lower) = _weights().weightsFor(t, epoch);
                uint256[][] memory raws = new uint256[][](count);
                uint256 k;
                for (uint256 i; i < calc.length; i++) {
                    if (calc[i].nodeType == t && calc[i].reason == SettlementReason.None && calc[i].raw.length > 0) {
                        raws[k++] = calc[i].raw;
                    }
                }
                uint256[] memory best = FactorScoring.bests(raws, lower);
                for (uint256 i; i < calc.length; i++) {
                    NodeCalc memory c = calc[i];
                    if (c.nodeType == t && c.reason == SettlementReason.None && c.raw.length > 0) {
                        c.score = FactorScoring.score(c.raw, best, weights, lower);
                        emit NodeScored(c.account, epoch, t, c.raw, c.score);
                    }
                }
            }
        }
        for (uint256 i; i < calc.length; i++) {
            if (calc[i].reason == SettlementReason.None && calc[i].score == 0) calc[i].reason = SettlementReason.Ineligible;
        }
    }

    function _finalizeReliability(IReliabilityRegistry rel, address node, uint256 epoch, uint256 heartbeatBps)
        internal
        returns (uint256 scoreBps, bool bonus)
    {
        if (heartbeatBps > 10_000) heartbeatBps = 10_000;
        return rel.finalizeEpoch(node, epoch, heartbeatBps);
    }

    /// @dev Pass 2 for one capacity type: score share, reliability and stage
    /// multipliers, then exact-sum bonus normalization over the adjusted shares.
    function _distributeType(NodeCalc[] memory calc, uint8 t, uint256 typeSum, uint256 pool) internal view {
        uint256 count;
        for (uint256 i = 0; i < calc.length; i++) {
            if (calc[i].reason == SettlementReason.None && calc[i].nodeType == t) count++;
        }
        uint256[] memory idx = new uint256[](count);
        uint256[] memory adjusted = new uint256[](count);
        bool[] memory bonus = new bool[](count);
        uint256 adjustedTotal;
        uint256 k;
        for (uint256 i = 0; i < calc.length; i++) {
            NodeCalc memory c = calc[i];
            if (c.reason != SettlementReason.None || c.nodeType != t) continue;
            uint256 share = (pool * c.score) / typeSum;
            adjusted[k] = (share * c.reliabilityBps * c.stagePenaltyBps) / 1e8;
            bonus[k] = c.bonus && adjusted[k] > 0;
            adjustedTotal += adjusted[k];
            idx[k++] = i;
        }

        uint256 bonusBps = config().getUint(ConfigKeys.REWARD_RELIABILITY_BONUS_BPS);
        if (bonusBps < 10_000) bonusBps = 10_000;
        uint256[] memory out = BonusNormalization.normalize(adjustedTotal, adjusted, bonus, bonusBps);
        for (uint256 j = 0; j < count; j++) {
            calc[idx[j]].amount = out[j];
        }
    }

    function _countReporter(uint256 epoch, address node) private {
        RewardStorage storage $ = _rewardStorage();
        if (!$.metrics[epoch][node].submitted) $.reporterCount[epoch] += 1;
    }

    function _countReportingNode(uint256 epoch, address node) private {
        RewardStorage storage $ = _rewardStorage();
        if ($.nodeReported[epoch][node]) return;
        $.nodeReported[epoch][node] = true;
        $.reportingNodeCount[epoch] += 1;
    }

    /// @notice #159: distinct nodes that reported for `epoch` (any service). Sealers
    /// compare this with the nodes found in `MetricReported`/`FactorMetricsReported` logs.
    function reportingNodeCount(uint256 epoch) external view returns (uint256) {
        return _rewardStorage().reportingNodeCount[epoch];
    }

    /// @notice Reward accounts with a metric report for `epoch` (one per reporting Node
    /// Service, #159); `sealEpoch` must cover all of them.
    function reporterCount(uint256 epoch) external view returns (uint256) {
        return _rewardStorage().reporterCount[epoch];
    }

    /// @notice Lowest epoch index `sealEpoch` will accept.
    function nextSealableEpoch() external view returns (uint256) {
        return _rewardStorage().nextSealableEpoch;
    }

    /// @notice Capacity SYM received but not assigned to any node (see `sealEpoch`).
    function undistributed() external view returns (uint256) {
        return _rewardStorage().undistributed;
    }

    /// @notice Governed release of unassigned SYM (e.g. an unclaimed verifier
    /// path or the ecosystem treasury). Bounded by the ledger, so it can
    /// never touch held reserves owed to nodes.
    function sweepUndistributed(address to, uint256 amount) external onlyTimelock {
        RewardStorage storage $ = _rewardStorage();
        if (amount > $.undistributed) revert SweepExceedsUndistributed(amount, $.undistributed);
        $.undistributed -= amount;
        IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS)).safeTransfer(to, amount);
        emit UndistributedSwept(to, amount);
    }

    /// @notice #80 FR10: the recorded settlement decision for (epoch, node).
    function settlementOf(uint256 epoch, address node) external view returns (SettlementRecord memory) {
        return _rewardStorage().settlements[epoch][node];
    }

    /// @notice SYM available to distribute for `epoch`. With an emission
    /// source configured (#73) this triggers the idempotent mint into this
    /// contract and uses the delivered amount; otherwise it falls back to the
    /// flat `REWARD_EMISSION_PER_EPOCH` (pre-#73 deployments and tests).
    function _epochEmission(uint256 epoch) internal returns (uint256) {
        address source = config().getAddress(ConfigKeys.REWARD_EMISSION_SOURCE);
        if (source == address(0)) return config().getUint(ConfigKeys.REWARD_EMISSION_PER_EPOCH);
        return INetworkRewardsEmission(source).emitForEpoch(epoch);
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

    /// @dev Reserve, streak and heartbeat state are per reward `account` (#159);
    /// tokens always go to `node`. Returns what was paid now and what was held.
    function _settle(address account, address node, uint256 epoch, uint256 amount)
        internal
        returns (uint256 paidNow, uint256 heldNow)
    {
        RewardStorage storage $ = _rewardStorage();
        uint256 thresholdBps = config().getUint(ConfigKeys.REWARD_HEARTBEAT_THRESHOLD_BPS);
        uint256 hb = $.metrics[epoch][account].heartbeatBps;

        // #53 FR5.2: strict ">90%" (not ">="), evaluated across the current
        // epoch AND the immediately preceding epoch. FR5.5: a node with no
        // preceding-epoch record for *itself* (not merely epoch index 0) is
        // judged on the current epoch's compliance alone for this one
        // payout — this correctly exempts a node whose first-ever eligible
        // epoch is e.g. epoch 5, not just epoch 0.
        bool hasPrevRecord = epoch > 0 && $.metrics[epoch - 1][account].submitted;
        bool ownEpochCompliant = hb > thresholdBps;
        bool autoPayEligible =
            ownEpochCompliant && (!hasPrevRecord || $.metrics[epoch - 1][account].heartbeatBps > thresholdBps);

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
            $.consecutiveCompliantEpochs[account] += 1;
        } else {
            $.consecutiveCompliantEpochs[account] = 0;
        }

        if (autoPayEligible) {
            uint256 toPay = amount;
            if ($.reserveBalance[account] > 0 && $.consecutiveCompliantEpochs[account] >= sweepEpochs) {
                toPay += $.reserveBalance[account];
                $.reserveBalance[account] = 0;
                emit ReserveSwept(node, toPay - amount);
            }

            paidNow = _payNode(node, epoch, toPay);
            emit Payout(node, epoch, paidNow);
        } else {
            // A non-auto-pay epoch can still complete the sweep streak (per
            // FR6.1's example: epoch X's own reading pushes the streak to
            // the threshold even though epoch X's *own* payout is held,
            // because epoch X's preceding epoch failed the two-epoch test).
            // Sweep independently of this epoch's own payout routing.
            if ($.reserveBalance[account] > 0 && $.consecutiveCompliantEpochs[account] >= sweepEpochs) {
                uint256 swept = $.reserveBalance[account];
                $.reserveBalance[account] = 0;
                paidNow = _payNode(node, epoch, swept);
                emit ReserveSwept(node, paidNow);
            }
            $.reserveBalance[account] += amount;
            heldNow = amount;
            emit ReserveCredit(node, epoch, amount);
        }
    }

    /// @notice Issue #57 §6 post-calculation payout hook: wraps (does not
    /// alter) the actual token transfer point. If `FOUNDATION_REWARD_HOOK`
    /// is unconfigured, or the hook reports the node is not a foundation
    /// node (or is at/below its operational cost baseline), the full
    /// `amount` is paid to `node` exactly as before this issue — the core
    /// reward calculation above this function is untouched either way.
    /// Otherwise the operator-visible amount is capped at the baseline and
    /// the surplus is routed to the ecosystem reserve address atomically,
    /// within this same settlement call.
    function _payNode(address node, uint256 epoch, uint256 amount) internal returns (uint256 paidToNode) {
        IERC20 token = IERC20(config().getAddress(ConfigKeys.TOKEN_ADDRESS));
        address hookAddr = config().getAddress(ConfigKeys.FOUNDATION_REWARD_HOOK);
        if (hookAddr == address(0)) {
            token.safeTransfer(node, amount);
            return amount;
        }

        (uint256 operatorAmount, uint256 reserveAmount, address reserveRecipient) =
            IFoundationRewardHook(hookAddr).routeFoundationPayout(node, epoch, amount);

        if (operatorAmount > 0) {
            token.safeTransfer(node, operatorAmount);
        }
        if (reserveAmount > 0 && reserveRecipient != address(0)) {
            token.safeTransfer(reserveRecipient, reserveAmount);
            emit FoundationSurplusRouted(node, epoch, reserveAmount, reserveRecipient);
        }
        return operatorAmount;
    }

    /// @notice Explicit manual claim of reserve balance ahead of the
    /// automatic compliance-triggered sweep. Per the Arch/QA resolution of
    /// open question 5, no early manual claim exists while non-compliant —
    /// this always reverts unless the consecutive-compliant-epoch condition
    /// has independently been met (in which case `sealEpoch`'s own sweep
    /// would already have paid it out; this is a safety-net entry point for
    /// a node that became compliant without a subsequent payout-bearing
    /// epoch being sealed for it).
    ///
    /// Routed through `_payNode`, so the #57 foundation hook applies here too.
    function claimReserve() external whenNotPaused {
        uint256 amount = _claimReserve(msg.sender);
        // The node's own account belongs to its primary service (#159), for the #161 ledger.
        INodeRegistry registry = _registry();
        if (address(registry) != address(0) && registry.hasPrimaryService(msg.sender)) {
            emit ServiceReserveClaimed(msg.sender, registry.typeOf(msg.sender), amount);
        }
    }

    /// @notice #159: the same safety-net claim for one of the caller's Node Services.
    function claimServiceReserve(INodeRegistry.NodeType service) external whenNotPaused {
        uint256 amount = _claimReserve(accountOf(msg.sender, service));
        emit ServiceReserveClaimed(msg.sender, service, amount);
    }

    function _claimReserve(address account) private returns (uint256 amount) {
        RewardStorage storage $ = _rewardStorage();
        uint256 sweepEpochs = config().getUint(ConfigKeys.REWARD_RESERVE_SWEEP_EPOCHS);
        if ($.reserveBalance[account] == 0 || $.consecutiveCompliantEpochs[account] < sweepEpochs) {
            revert NoReserveToSweep();
        }
        amount = $.reserveBalance[account];
        $.reserveBalance[account] = 0;
        // Through the #57 foundation hook like every other payout (closes the known gap).
        _payNode(msg.sender, $.nextSealableEpoch == 0 ? 0 : $.nextSealableEpoch - 1, amount);
        emit ReserveSwept(msg.sender, amount);
    }
}
