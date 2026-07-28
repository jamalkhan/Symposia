// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";

/// @notice Narrow slice of `FoundationRegistry`'s ABI this contract calls
/// into for Phase 1 witnessing (issue #89 Arch: "Foundation Verifiers now,
/// VRF-selected community quorum later, same call shape"). Declared here
/// rather than importing the concrete contract so this file only depends on
/// the call shapes it actually uses, matching the pattern `FoundationRegistry`
/// itself already established for its own `StakingNodeRegistry` dependency.
interface IFoundationWitnessSource {
    function isFoundationNode(address node) external view returns (bool);
    function getFoundationRegion(address node) external view returns (bytes32);
}

/// @title ComputeTierRegistry
/// @notice Issue #89: verifier-witnessed compute-node hardware benchmarking
/// and Compute Tier 1/2/3 classification. Consumes the witness-selection
/// rules from `verifier-nodes.md` (minimum quorum scaling with pool size,
/// >=2 regions represented, no self-verification) against a Foundation
/// Verifier witness pool during Phase 1, per the Arch plan's "same interface,
/// different caller population, no invented trust-reduction" design. Tier
/// classification (`classifyTier`) is a pure, all-thresholds-met function
/// over governance-configured per-tier minimums (FR3/AC2), not a weighted
/// score. The `symposia-computed` daemon (#88) executes and reports
/// measurements; it never classifies itself — classification only happens
/// here, which is what makes "not self-reported" architectural rather than
/// policy.
contract ComputeTierRegistry is GovernedUpgradeable {
    enum ComputeTier {
        Rejected,
        Tier3,
        Tier2,
        Tier1
    }

    struct Measured {
        uint256 mips;
        uint256 ramBandwidthGBs;
        uint256 iopsRandomRead;
        uint256 peerRttMs;
    }

    struct BenchmarkAttestation {
        uint256 epoch;
        Measured measured;
        uint16 declaredCores;
        uint32 declaredRamGB;
        uint16 observedCores;
        uint32 observedRamGB;
        address[] witnessSet;
        ComputeTier resultTier;
        bool hardwareMismatch;
        uint256 recordedAt;
    }

    /// @custom:storage-location erc7201:symposia.ComputeTierRegistry
    struct ComputeTierStorage {
        mapping(address => ComputeTier) currentTier;
        mapping(address => uint256) lastVerifiedEpoch;
        mapping(address => bool) everClassified;
    }

    bytes32 private constant COMPUTE_TIER_STORAGE_LOCATION = keccak256("symposia.storage.ComputeTierRegistry");

    event BenchmarkAttested(address indexed node, uint256 indexed epoch, ComputeTier resultTier, bool hardwareMismatch);
    event TierDowngraded(
        address indexed node, ComputeTier fromTier, ComputeTier toTier, bytes32 evidenceRef, uint256 effectiveEpoch
    );
    event BenchmarkFailed(address indexed node, bytes32 evidenceRef);

    error InsufficientWitnessQuorum(uint256 provided, uint256 required);
    error InsufficientGeographicDiversity();
    error WitnessNotFoundationVerifier(address witness);
    error SelfVerificationNotAllowed(address node);
    error DuplicateWitness(address witness);
    error NotYetClassified(address node);

    function _storage() private pure returns (ComputeTierStorage storage $) {
        bytes32 slot = COMPUTE_TIER_STORAGE_LOCATION;
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

    // --- Views ---

    function currentTierOf(address node) external view returns (ComputeTier) {
        return _storage().currentTier[node];
    }

    function lastVerifiedEpochOf(address node) external view returns (uint256) {
        return _storage().lastVerifiedEpoch[node];
    }

    /// @notice No node may go more than `COMPUTE_TIER_MAX_EPOCHS_BETWEEN_VERIFICATIONS`
    /// epochs (default 7, per verifier-nodes.md) without a re-verification (AC6).
    /// A never-classified node is always due.
    function isDueForReverification(address node, uint256 currentEpoch) external view returns (bool) {
        ComputeTierStorage storage $ = _storage();
        if (!$.everClassified[node]) return true;
        uint256 maxEpochs = config().getUint(ConfigKeys.COMPUTE_TIER_MAX_EPOCHS_BETWEEN_VERIFICATIONS);
        return currentEpoch - $.lastVerifiedEpoch[node] >= maxEpochs;
    }

    /// @notice Pure classification function (FR3/AC1-AC3): a node is
    /// classified at the highest tier for which every one of the six
    /// dimensions (declared cores, measured MIPS, declared RAM, measured RAM
    /// bandwidth, measured IOPS, measured RTT) clears that tier's minimum.
    /// Falling short of Tier 3 on any dimension yields `Rejected`, not a
    /// lower tier (Gherkin: "Node fails minimum requirements and is
    /// rejected").
    function classifyTier(Measured memory measured, uint16 cores, uint32 ramGB) public view returns (ComputeTier) {
        if (_clearsTier(measured, cores, ramGB, ComputeTier.Tier1)) return ComputeTier.Tier1;
        if (_clearsTier(measured, cores, ramGB, ComputeTier.Tier2)) return ComputeTier.Tier2;
        if (_clearsTier(measured, cores, ramGB, ComputeTier.Tier3)) return ComputeTier.Tier3;
        return ComputeTier.Rejected;
    }

    function _clearsTier(Measured memory measured, uint16 cores, uint32 ramGB, ComputeTier tier)
        private
        view
        returns (bool)
    {
        uint8 t = uint8(tier);
        return cores >= config().getUint(ConfigKeys.computeTierMinCores(t))
            && measured.mips >= config().getUint(ConfigKeys.computeTierMinMips(t))
            && ramGB >= config().getUint(ConfigKeys.computeTierMinRamGB(t))
            && measured.ramBandwidthGBs >= config().getUint(ConfigKeys.computeTierMinRamBandwidthGBs(t))
            && measured.iopsRandomRead >= config().getUint(ConfigKeys.computeTierMinIops(t))
            && measured.peerRttMs <= config().getUint(ConfigKeys.computeTierMaxRttMs(t));
    }

    /// @notice Minimum witness quorum for a given Foundation Verifier pool
    /// size, per verifier-nodes.md's quorum-scaling brackets: 3-10 -> 3,
    /// 11-30 -> 5, 31-100 -> 7, >100 -> 10.
    function minQuorumFor(uint256 poolSize) public pure returns (uint256) {
        if (poolSize <= 10) return 3;
        if (poolSize <= 30) return 5;
        if (poolSize <= 100) return 7;
        return 10;
    }

    // --- Submission ---

    /// @notice Records a verifier-witnessed benchmark result and returns the
    /// resulting classification (FR2-FR3, FR7-FR8). `poolSize` is the
    /// current size of the witness pool the caller drew `witnessSet` from
    /// (Foundation Verifier count during Phase 1), used only to determine
    /// the required quorum bracket.
    function submitBenchmarkAttestation(
        address node,
        uint256 epoch,
        Measured calldata measured,
        uint16 declaredCores,
        uint32 declaredRamGB,
        uint16 observedCores,
        uint32 observedRamGB,
        address[] calldata witnessSet,
        uint256 poolSize,
        IFoundationWitnessSource witnessSource
    ) external whenNotPaused returns (ComputeTier) {
        _validateWitnesses(node, witnessSet, poolSize, witnessSource);

        bytes32 evidenceRef = keccak256(abi.encode(node, epoch, measured, declaredCores, declaredRamGB, witnessSet));

        bool mismatch = _hardwareMismatch(declaredCores, observedCores, declaredRamGB, observedRamGB);

        ComputeTier resultTier;
        if (mismatch) {
            resultTier = ComputeTier.Rejected;
        } else {
            resultTier = classifyTier(measured, observedCores, observedRamGB);
        }

        ComputeTierStorage storage $ = _storage();
        ComputeTier previousTier = $.currentTier[node];
        bool wasClassified = $.everClassified[node];

        $.lastVerifiedEpoch[node] = epoch;
        $.everClassified[node] = true;

        if (resultTier == ComputeTier.Rejected) {
            emit BenchmarkFailed(node, evidenceRef);
            emit BenchmarkAttested(node, epoch, resultTier, mismatch);
            // A rejected re-verification demotes an already-classified node
            // below Tier 3 (flagged for #91's penalty staging) rather than
            // leaving its prior (now-unearned) tier in place.
            if (wasClassified && previousTier != ComputeTier.Rejected) {
                $.currentTier[node] = ComputeTier.Rejected;
                emit TierDowngraded(node, previousTier, ComputeTier.Rejected, evidenceRef, epoch + 1);
            }
            return resultTier;
        }

        $.currentTier[node] = resultTier;
        emit BenchmarkAttested(node, epoch, resultTier, mismatch);

        if (wasClassified && resultTier < previousTier) {
            emit TierDowngraded(node, previousTier, resultTier, evidenceRef, epoch + 1);
        }

        return resultTier;
    }

    function _validateWitnesses(
        address node,
        address[] calldata witnessSet,
        uint256 poolSize,
        IFoundationWitnessSource witnessSource
    ) private view {
        uint256 required = minQuorumFor(poolSize);
        if (witnessSet.length < required) revert InsufficientWitnessQuorum(witnessSet.length, required);

        bytes32 firstRegion;
        bool sawSecondRegion = false;

        for (uint256 i = 0; i < witnessSet.length; i++) {
            address witness = witnessSet[i];
            if (witness == node) revert SelfVerificationNotAllowed(node);
            if (!witnessSource.isFoundationNode(witness)) revert WitnessNotFoundationVerifier(witness);

            for (uint256 j = 0; j < i; j++) {
                if (witnessSet[j] == witness) revert DuplicateWitness(witness);
            }

            bytes32 region = witnessSource.getFoundationRegion(witness);
            if (i == 0) {
                firstRegion = region;
            } else if (region != firstRegion) {
                sawSecondRegion = true;
            }
        }

        // Single-witness quorums (should not occur once `minQuorumFor` returns
        // >=3, but guarded explicitly) trivially satisfy diversity.
        if (witnessSet.length > 1 && !sawSecondRegion) revert InsufficientGeographicDiversity();
    }

    /// @notice FR8: operator-declared cores/RAM cross-checked against the
    /// witness's own `/hostinfo` probe (#88). A mismatch beyond the
    /// configured tolerance is treated as a failed benchmark rather than
    /// accepted at face value.
    function _hardwareMismatch(uint16 declaredCores, uint16 observedCores, uint32 declaredRamGB, uint32 observedRamGB)
        private
        view
        returns (bool)
    {
        uint256 toleranceBps = config().getUint(ConfigKeys.COMPUTE_TIER_HARDWARE_TOLERANCE_BPS);
        return _exceedsTolerance(declaredCores, observedCores, toleranceBps)
            || _exceedsTolerance(declaredRamGB, observedRamGB, toleranceBps);
    }

    function _exceedsTolerance(uint256 declared, uint256 observed, uint256 toleranceBps) private pure returns (bool) {
        uint256 diff = declared >= observed ? declared - observed : observed - declared;
        if (declared == 0) return diff != 0;
        return (diff * 10_000) / declared > toleranceBps;
    }
}
