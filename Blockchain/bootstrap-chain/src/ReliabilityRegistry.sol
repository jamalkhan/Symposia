// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {IReliabilityRegistry} from "./interfaces/IReliabilityRegistry.sol";

/// @title ReliabilityRegistry
/// @notice Per-node reliability score and trailing-window bonus eligibility
/// (issue #79, tokenomics-mvp.md §6.4). Fixed-point basis points throughout
/// (1.0 = 10_000), so every client recomputes identical values.
///
/// Score model (all deltas are governed config, applied prospectively):
/// - A node starts at 1.0. Storage holds a *deficit* (0 for a new node), so
///   no registration hook is needed.
/// - `recordChallengeOutcome` (PoR / resource probe) and `recordTimeout` are
///   called by the challenge issuer. Each failure immediately subtracts its
///   fixed delta (`RELIABILITY_DELTA_*_BPS`), clamped at 0. A replayed
///   `(node, epoch, kind, nonce)` reverts, so redundant verifier reports
///   can't double-degrade. Passed challenges are recorded with no score
///   effect.
/// - `finalizeEpoch` is called once per node per epoch by the reward
///   distributor at seal. It applies heartbeat degradation of
///   `RELIABILITY_DELTA_HEARTBEAT_BPS_PER_PCT` per full percentage point of
///   missed heartbeats (from #75's compliance figure). If the epoch had no
///   degradation at all, it adds `RELIABILITY_RECOVERY_BPS` (gradual climb,
///   capped at 1.0). Then it snapshots the score for (node, epoch).
///   Reliability recovery is deliberately continuous. #82's "3 consecutive
///   clean epochs" stage recovery is a separate, discrete gate reading the
///   same clean/dirty signal.
///
/// Bonus eligibility: the score must have been >= the threshold (0.98) at
/// the end of **each** of the trailing `RELIABILITY_BONUS_WINDOW` (30)
/// epochs, including the current one. This is tracked as an O(1) streak
/// counter. Any epoch below threshold, or any gap in the node's finalized
/// epochs (an epoch the node wasn't sealed for), resets the streak. A node
/// with fewer than 30 epochs of history is therefore never eligible.
///
/// Failures are counted twice on purpose: every outcome is emitted as an
/// event that #82's stage evaluator can also consume (a small continuous
/// reliability ding here, plus a possible discrete Stage 1 trigger there).
/// This contract never calls into #82.
contract ReliabilityRegistry is GovernedUpgradeable, IReliabilityRegistry {
    uint256 internal constant ONE_BPS = 10_000;

    enum ChallengeKind {
        Por,
        ResourceProbe,
        Timeout
    }

    struct NodeState {
        uint16 deficitBps; // 10_000 - current score
        uint16 highStreak; // consecutive finalized epochs ending >= threshold
        bool hasFinalized;
        uint64 lastFinalizedEpoch;
    }

    struct EpochRecord {
        uint16 scoreBps;
        bool finalized;
        bool bonusEligible;
        bool degraded; // any failure or heartbeat ding this epoch
    }

    /// @custom:storage-location erc7201:symposia.ReliabilityRegistry
    struct ReliabilityStorage {
        mapping(address => NodeState) nodes;
        mapping(address => mapping(uint256 => EpochRecord)) epochs;
        mapping(bytes32 => bool) seenOutcome;
    }

    // keccak256(abi.encode(uint256(keccak256("symposia.ReliabilityRegistry")) - 1)) & ~bytes32(uint256(0xff))
    bytes32 private constant RELIABILITY_STORAGE_LOCATION =
        0x378195eb34af454070b69bab8e35fa427f61bc6c5c3c671d0f706e8ee865b900;

    event ChallengeRecorded(
        address indexed node,
        uint256 indexed epoch,
        ChallengeKind indexed kind,
        bool passed,
        bytes32 evidenceRef,
        uint256 nonce,
        uint256 deltaBps,
        uint256 scoreBps
    );
    event EpochFinalized(
        address indexed node,
        uint256 indexed epoch,
        uint256 scoreBps,
        uint256 heartbeatDeltaBps,
        uint256 recoveryBps,
        bool bonusEligible
    );

    error NotChallenger(address caller);
    error NotFinalizer(address caller);
    error DuplicateOutcome(bytes32 key);
    error EpochAlreadyFinalized(address node, uint256 epoch);
    error EpochNotFinalized(address node, uint256 epoch);
    error OutOfOrderEpoch(address node, uint256 epoch, uint256 lastFinalized);
    error InvalidHeartbeat(uint256 heartbeatBps);
    error TimeoutViaChallenge();

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        _disableInitializers();
    }

    function initialize(IProtocolConfig initialConfig) external initializer {
        __GovernedUpgradeable_init(initialConfig);
    }

    function _rs() private pure returns (ReliabilityStorage storage $) {
        bytes32 slot = RELIABILITY_STORAGE_LOCATION;
        assembly {
            $.slot := slot
        }
    }

    // ---------------------------------------------------------------------
    // Recording
    // ---------------------------------------------------------------------

    /// @notice Records a PoR or resource-probe outcome. The PoR / probe
    /// cryptography lives with the issuer. `evidenceRef` is a commitment
    /// into the issuer's evidence store for disputes.
    function recordChallengeOutcome(
        address node,
        uint256 epoch,
        ChallengeKind kind,
        bool passed,
        bytes32 evidenceRef,
        uint256 nonce
    ) external whenNotPaused {
        if (kind == ChallengeKind.Timeout) revert TimeoutViaChallenge();
        _record(node, epoch, kind, passed, evidenceRef, nonce);
    }

    /// @notice Records a request timeout attributed to the node (not the client).
    function recordTimeout(address node, uint256 epoch, bytes32 evidenceRef, uint256 nonce) external whenNotPaused {
        _record(node, epoch, ChallengeKind.Timeout, false, evidenceRef, nonce);
    }

    function _record(address node, uint256 epoch, ChallengeKind kind, bool passed, bytes32 evidenceRef, uint256 nonce)
        private
    {
        if (msg.sender != config().getAddress(ConfigKeys.RELIABILITY_CHALLENGER)) revert NotChallenger(msg.sender);
        ReliabilityStorage storage $ = _rs();
        NodeState storage n = $.nodes[node];
        if (n.hasFinalized && epoch <= n.lastFinalizedEpoch) revert EpochAlreadyFinalized(node, epoch);

        bytes32 key = keccak256(abi.encode(node, epoch, kind, nonce));
        if ($.seenOutcome[key]) revert DuplicateOutcome(key);
        $.seenOutcome[key] = true;

        uint256 delta;
        if (!passed) {
            delta = config().getUint(_deltaKey(kind));
            delta = _degrade(n, delta);
            $.epochs[node][epoch].degraded = true;
        }
        emit ChallengeRecorded(node, epoch, kind, passed, evidenceRef, nonce, delta, ONE_BPS - n.deficitBps);
    }

    /// @notice Closes `epoch` for `node`: heartbeat degradation, recovery,
    /// score snapshot and bonus eligibility. Called by RewardDistributor at
    /// seal. Epochs must be finalized in increasing order per node.
    function finalizeEpoch(address node, uint256 epoch, uint256 heartbeatBps)
        external
        whenNotPaused
        returns (uint256 scoreBps, bool bonusEligible)
    {
        if (msg.sender != config().getAddress(ConfigKeys.RELIABILITY_FINALIZER)) revert NotFinalizer(msg.sender);
        if (heartbeatBps > ONE_BPS) revert InvalidHeartbeat(heartbeatBps);
        ReliabilityStorage storage $ = _rs();
        NodeState storage n = $.nodes[node];
        if (n.hasFinalized && epoch <= n.lastFinalizedEpoch) revert OutOfOrderEpoch(node, epoch, n.lastFinalizedEpoch);

        EpochRecord storage rec = $.epochs[node][epoch];

        uint256 missedPct = (ONE_BPS - heartbeatBps) / 100;
        uint256 heartbeatDelta;
        if (missedPct > 0) {
            heartbeatDelta = _degrade(n, missedPct * config().getUint(ConfigKeys.RELIABILITY_DELTA_HEARTBEAT_BPS_PER_PCT));
            rec.degraded = true;
        }

        uint256 recovery;
        if (!rec.degraded && n.deficitBps > 0) {
            recovery = config().getUint(ConfigKeys.RELIABILITY_RECOVERY_BPS);
            if (recovery > n.deficitBps) recovery = n.deficitBps;
            n.deficitBps -= uint16(recovery);
        }

        scoreBps = ONE_BPS - n.deficitBps;
        bool contiguous = !n.hasFinalized || epoch == n.lastFinalizedEpoch + 1;
        if (scoreBps >= config().getUint(ConfigKeys.REWARD_RELIABILITY_BONUS_THRESHOLD_BPS)) {
            uint16 base = contiguous ? n.highStreak : 0;
            n.highStreak = base == type(uint16).max ? base : base + 1;
        } else {
            n.highStreak = 0;
        }
        bonusEligible = n.highStreak >= config().getUint(ConfigKeys.RELIABILITY_BONUS_WINDOW);

        n.hasFinalized = true;
        n.lastFinalizedEpoch = uint64(epoch);
        rec.scoreBps = uint16(scoreBps);
        rec.finalized = true;
        rec.bonusEligible = bonusEligible;
        emit EpochFinalized(node, epoch, scoreBps, heartbeatDelta, recovery, bonusEligible);
    }

    // ---------------------------------------------------------------------
    // Views
    // ---------------------------------------------------------------------

    /// @notice Live score in bps (1.0 = 10_000), including in-flight failures
    /// for the current, not-yet-finalized epoch.
    function currentScoreBps(address node) external view returns (uint256) {
        return ONE_BPS - _rs().nodes[node].deficitBps;
    }

    /// @inheritdoc IReliabilityRegistry
    function reliabilityOf(address node, uint256 epoch) external view returns (uint256 scoreBps, bool bonusEligible) {
        EpochRecord memory rec = _rs().epochs[node][epoch];
        if (!rec.finalized) revert EpochNotFinalized(node, epoch);
        return (rec.scoreBps, rec.bonusEligible);
    }

    function isFinalized(address node, uint256 epoch) external view returns (bool) {
        return _rs().epochs[node][epoch].finalized;
    }

    function highStreakOf(address node) external view returns (uint256) {
        return _rs().nodes[node].highStreak;
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    function _deltaKey(ChallengeKind kind) private pure returns (bytes32) {
        if (kind == ChallengeKind.Por) return ConfigKeys.RELIABILITY_DELTA_POR_BPS;
        if (kind == ChallengeKind.ResourceProbe) return ConfigKeys.RELIABILITY_DELTA_PROBE_BPS;
        return ConfigKeys.RELIABILITY_DELTA_TIMEOUT_BPS;
    }

    /// @dev Applies `delta` to the deficit, clamping the score at 0. Returns the delta actually applied.
    function _degrade(NodeState storage n, uint256 delta) private returns (uint256 applied) {
        uint256 headroom = ONE_BPS - n.deficitBps;
        applied = delta > headroom ? headroom : delta;
        n.deficitBps += uint16(applied);
    }
}
