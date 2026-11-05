// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {MerkleProof} from "@openzeppelin/contracts/utils/cryptography/MerkleProof.sol";
import {EIP712Upgradeable} from "@openzeppelin/contracts-upgradeable/utils/cryptography/EIP712Upgradeable.sol";
import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";

/// @notice `IProtocolConfig` keys owned by the telemetry pipeline (issue #75).
library TelemetryConfigKeys {
    /// @notice Checkpoint windows per epoch (MVP: 24, i.e. hourly for a 24h epoch).
    bytes32 internal constant CHECKPOINTS_PER_EPOCH = keccak256("telemetry.checkpointsPerEpoch");
    /// @notice Seconds after a checkpoint window closes before a node may
    /// self-checkpoint that window because the committee did not.
    bytes32 internal constant SELF_CHECKPOINT_GRACE = keccak256("telemetry.selfCheckpointGrace");
    /// @notice Self-reported vs verifier-attested uptime gap (bps of 10_000) that sets `divergenceFlag`.
    bytes32 internal constant DIVERGENCE_UPTIME_BPS = keccak256("telemetry.divergence.uptimeBps");
    /// @notice Relative latency gap (bps of the verifier value) that sets `divergenceFlag`.
    bytes32 internal constant DIVERGENCE_LATENCY_BPS = keccak256("telemetry.divergence.latencyBps");
}

/// @title TelemetryCheckpointRegistry
/// @notice On-chain half of the sub-epoch metrics pipeline (issue #75).
///
/// Nodes gossip a signed `HeartbeatSample` every 5 minutes and verifiers
/// gossip signed `Attestation`s (off-chain, see Symposia.Network.Node
/// Telemetry). This contract holds only commitments to that data:
///
/// 1. `commitCheckpoint` — an N-of-M threshold of the Checkpoint Committee
///    commits a Merkle root over one window's canonically sorted samples,
///    plus the content address of the raw signed batch in blob storage. Chain
///    writes scale with windows per epoch (default 24), not with samples.
///    Checkpoints are accepted until the epoch's finality deadline, so
///    samples arriving inside #72's finality window are still incorporated
///    and anything later is rejected.
/// 2. `submitAggregatedRecords` — after the finality deadline, the committee
///    threshold-signs one `AggregatedMetricRecord` per node, produced by the
///    deterministic median+MAD aggregator. Each record is written once and is
///    the stable interface #76/#80 read via `getEpochMetricRecord`. Compliance
///    bps, eligibility and the divergence flag are derived here from the
///    submitted counts and values rather than trusted from the input.
/// 3. `verifySampleInclusion` — the hook #79 challenges use: prove a specific
///    signed sample (leaf) was committed in a checkpoint.
///
/// Committee: a governed membership list and majority threshold
/// (`setCommittee`, Timelock-only). The Arch plan's VRF-rotated committee
/// (reusing verifier selection) plugs in by having governance, or a future
/// rotation contract set as Timelock-delegate, call `setCommittee`; the
/// signature checks here don't change.
///
/// Liveness fallback (Arch risk "committee liveness/censorship"): if the
/// committee hasn't checkpointed window `i` by `windowEnd(i) + grace`, any
/// Active registered node may `selfCheckpoint` its own samples for that
/// window at its own gas cost. Self-checkpoints are keyed per node, so one
/// node can never commit for another and the committee path stays primary.
contract TelemetryCheckpointRegistry is GovernedUpgradeable, EIP712Upgradeable {
    struct TelemetryCheckpoint {
        bytes32 merkleRoot;
        bytes32 blobAddress; // content address (hash) of the raw signed batch
        uint32 sampleCount;
        uint64 committedAt;
    }

    /// @notice Per-field aggregated values. Units match the off-chain
    /// `HeartbeatSample`: throughput in KB/s, bandwidth in kbit/s, latency
    /// and retrieval time in ms, uptime in bps. Zero means "not measured".
    struct MetricValues {
        uint32 uptimeBps;
        uint64 readThroughputKBps;
        uint64 writeThroughputKBps;
        uint64 bandwidthInKbps;
        uint64 bandwidthOutKbps;
        uint32 latencyMs;
        uint32 retrievalMs;
    }

    /// @notice What the committee signs for one node.
    struct AggregatedMetricInput {
        address node;
        MetricValues selfReported;
        MetricValues verifierAttested;
        uint32 heartbeatsReceived;
        uint32 heartbeatsExpected;
        uint16 attestationCount;
        uint16 firstCheckpoint;
        uint16 lastCheckpoint;
    }

    /// @notice The per-node epoch metric record consumed by #76/#79/#80.
    struct AggregatedMetricRecord {
        MetricValues selfReported;
        MetricValues verifierAttested;
        uint32 heartbeatsReceived;
        uint32 heartbeatsExpected;
        uint16 heartbeatComplianceBps;
        uint16 attestationCount;
        uint16 firstCheckpoint;
        uint16 lastCheckpoint;
        bool eligible;
        bool divergenceFlag;
        bool exists;
    }

    /// @custom:storage-location erc7201:symposia.TelemetryCheckpointRegistry
    struct TelemetryStorage {
        mapping(uint256 => mapping(uint256 => TelemetryCheckpoint)) checkpoints;
        mapping(uint256 => uint256) checkpointCount;
        mapping(uint256 => mapping(uint256 => mapping(address => TelemetryCheckpoint))) selfCheckpoints;
        mapping(uint256 => mapping(address => AggregatedMetricRecord)) records;
        mapping(address => uint256) memberGeneration;
        uint256 generation;
        uint256 threshold;
        address[] members;
    }

    bytes32 private constant TELEMETRY_STORAGE_LOCATION = keccak256("symposia.storage.TelemetryCheckpointRegistry");

    bytes32 public constant CHECKPOINT_TYPEHASH = keccak256(
        "TelemetryCheckpoint(uint256 epochIndex,uint256 checkpointIndex,bytes32 merkleRoot,bytes32 blobAddress,uint32 sampleCount)"
    );
    bytes32 public constant AGGREGATE_TYPEHASH =
        keccak256("AggregatedRecords(uint256 epochIndex,bytes32 recordsHash)");

    uint256 internal constant DEFAULT_CHECKPOINTS_PER_EPOCH = 24;

    event CommitteeSet(uint256 indexed generation, address[] members, uint256 threshold);
    event CheckpointCommitted(
        uint256 indexed epoch, uint256 indexed checkpointIndex, bytes32 merkleRoot, bytes32 blobAddress, uint32 sampleCount
    );
    event SelfCheckpointCommitted(
        uint256 indexed epoch, uint256 indexed checkpointIndex, address indexed node, bytes32 merkleRoot, bytes32 blobAddress
    );
    event AggregatedRecordWritten(
        uint256 indexed epoch, address indexed node, uint16 heartbeatComplianceBps, bool eligible, bool divergenceFlag
    );

    error InvalidCommittee();
    error NoCommittee();
    error InsufficientSignatures(uint256 valid, uint256 threshold);
    error SignersNotSorted();
    error NotCommitteeMember(address signer);
    error CheckpointIndexOutOfRange(uint256 index, uint256 perEpoch);
    error CheckpointAlreadyCommitted(uint256 epoch, uint256 index);
    error FinalityWindowElapsed(uint256 epoch);
    error FinalityWindowNotElapsed(uint256 epoch);
    error WindowNotClosed(uint256 epoch, uint256 index);
    error SelfCheckpointNotYetAllowed(uint256 epoch, uint256 index);
    error NotActiveNode(address node);
    error RecordAlreadyWritten(uint256 epoch, address node);
    error InvalidRecord(address node);
    error EmptyCheckpoint();

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        _disableInitializers();
    }

    function initialize(IProtocolConfig initialConfig, address[] calldata members, uint256 threshold_)
        external
        initializer
    {
        __GovernedUpgradeable_init(initialConfig);
        __EIP712_init("SymposiaTelemetry", "1");
        _setCommittee(members, threshold_);
    }

    // ---------------------------------------------------------------------
    // Committee
    // ---------------------------------------------------------------------

    /// @notice Replaces the committee. The threshold must be a strict
    /// majority so no minority of members can checkpoint or censor alone.
    function setCommittee(address[] calldata members, uint256 threshold_) external onlyTimelock {
        _setCommittee(members, threshold_);
    }

    function _setCommittee(address[] calldata members, uint256 threshold_) private {
        uint256 n = members.length;
        if (n == 0 || threshold_ > n || threshold_ * 2 <= n) revert InvalidCommittee();
        TelemetryStorage storage $ = _ts();
        uint256 gen = ++$.generation;
        for (uint256 i; i < n; ++i) {
            if (members[i] == address(0) || $.memberGeneration[members[i]] == gen) revert InvalidCommittee();
            $.memberGeneration[members[i]] = gen;
        }
        $.members = members;
        $.threshold = threshold_;
        emit CommitteeSet(gen, members, threshold_);
    }

    function committee() external view returns (address[] memory members, uint256 threshold_) {
        TelemetryStorage storage $ = _ts();
        return ($.members, $.threshold);
    }

    function isCommitteeMember(address account) public view returns (bool) {
        TelemetryStorage storage $ = _ts();
        return $.generation != 0 && $.memberGeneration[account] == $.generation;
    }

    // ---------------------------------------------------------------------
    // Checkpoints
    // ---------------------------------------------------------------------

    /// @notice Commits one window's sample batch. `signatures` must come from
    /// distinct committee members, ordered by strictly ascending signer address.
    function commitCheckpoint(
        uint256 epoch,
        uint256 checkpointIndex,
        bytes32 merkleRoot,
        bytes32 blobAddress,
        uint32 sampleCount,
        bytes[] calldata signatures
    ) external whenNotPaused {
        _checkIndex(checkpointIndex);
        if (block.timestamp >= epochFinalityDeadline(epoch)) revert FinalityWindowElapsed(epoch);
        if (merkleRoot == bytes32(0) || sampleCount == 0) revert EmptyCheckpoint();
        TelemetryStorage storage $ = _ts();
        if ($.checkpoints[epoch][checkpointIndex].committedAt != 0) {
            revert CheckpointAlreadyCommitted(epoch, checkpointIndex);
        }

        bytes32 digest = _hashTypedDataV4(
            keccak256(abi.encode(CHECKPOINT_TYPEHASH, epoch, checkpointIndex, merkleRoot, blobAddress, sampleCount))
        );
        _requireThreshold(digest, signatures);

        $.checkpoints[epoch][checkpointIndex] = TelemetryCheckpoint({
            merkleRoot: merkleRoot,
            blobAddress: blobAddress,
            sampleCount: sampleCount,
            committedAt: uint64(block.timestamp)
        });
        $.checkpointCount[epoch] += 1;
        emit CheckpointCommitted(epoch, checkpointIndex, merkleRoot, blobAddress, sampleCount);
    }

    /// @notice Liveness fallback: a registered, Active node commits its own
    /// samples for a window the committee missed. Only allowed from
    /// `windowEnd + grace` until the epoch's finality deadline.
    function selfCheckpoint(
        uint256 epoch,
        uint256 checkpointIndex,
        bytes32 merkleRoot,
        bytes32 blobAddress,
        uint32 sampleCount
    ) external whenNotPaused {
        _checkIndex(checkpointIndex);
        if (block.timestamp >= epochFinalityDeadline(epoch)) revert FinalityWindowElapsed(epoch);
        if (merkleRoot == bytes32(0) || sampleCount == 0) revert EmptyCheckpoint();
        TelemetryStorage storage $ = _ts();
        if ($.checkpoints[epoch][checkpointIndex].committedAt != 0) {
            revert CheckpointAlreadyCommitted(epoch, checkpointIndex);
        }
        uint256 grace = config().getUint(TelemetryConfigKeys.SELF_CHECKPOINT_GRACE);
        if (block.timestamp < checkpointWindowEnd(epoch, checkpointIndex) + grace) {
            revert SelfCheckpointNotYetAllowed(epoch, checkpointIndex);
        }
        INodeRegistry registry = INodeRegistry(config().getAddress(ConfigKeys.REGISTRY_ADDRESS));
        if (address(registry) == address(0) || registry.statusOf(msg.sender) != INodeRegistry.NodeStatus.Active) {
            revert NotActiveNode(msg.sender);
        }
        if ($.selfCheckpoints[epoch][checkpointIndex][msg.sender].committedAt != 0) {
            revert CheckpointAlreadyCommitted(epoch, checkpointIndex);
        }

        $.selfCheckpoints[epoch][checkpointIndex][msg.sender] = TelemetryCheckpoint({
            merkleRoot: merkleRoot,
            blobAddress: blobAddress,
            sampleCount: sampleCount,
            committedAt: uint64(block.timestamp)
        });
        emit SelfCheckpointCommitted(epoch, checkpointIndex, msg.sender, merkleRoot, blobAddress);
    }

    function getCheckpoint(uint256 epoch, uint256 checkpointIndex) external view returns (TelemetryCheckpoint memory) {
        return _ts().checkpoints[epoch][checkpointIndex];
    }

    function getSelfCheckpoint(uint256 epoch, uint256 checkpointIndex, address node)
        external
        view
        returns (TelemetryCheckpoint memory)
    {
        return _ts().selfCheckpoints[epoch][checkpointIndex][node];
    }

    function checkpointCount(uint256 epoch) external view returns (uint256) {
        return _ts().checkpointCount[epoch];
    }

    /// @notice True if `leaf` (keccak256 of a signed sample's canonical
    /// encoding, see `TelemetryMerkle` off-chain) is in the committee
    /// checkpoint, or — if `node` is non-zero — in that node's self-checkpoint.
    /// Uses OpenZeppelin's sorted-pair Merkle convention.
    function verifySampleInclusion(
        uint256 epoch,
        uint256 checkpointIndex,
        address node,
        bytes32 leaf,
        bytes32[] calldata proof
    ) external view returns (bool) {
        TelemetryStorage storage $ = _ts();
        bytes32 root = node == address(0)
            ? $.checkpoints[epoch][checkpointIndex].merkleRoot
            : $.selfCheckpoints[epoch][checkpointIndex][node].merkleRoot;
        return root != bytes32(0) && MerkleProof.verifyCalldata(proof, root, leaf);
    }

    // ---------------------------------------------------------------------
    // Aggregated records
    // ---------------------------------------------------------------------

    /// @notice Writes the committee-signed aggregated records for `epoch`.
    /// Only after the finality deadline (so every in-window sample could be
    /// checkpointed), and at most once per (node, epoch).
    function submitAggregatedRecords(uint256 epoch, AggregatedMetricInput[] calldata inputs, bytes[] calldata signatures)
        external
        whenNotPaused
    {
        if (block.timestamp < epochFinalityDeadline(epoch)) revert FinalityWindowNotElapsed(epoch);
        bytes32 digest = _hashTypedDataV4(keccak256(abi.encode(AGGREGATE_TYPEHASH, epoch, hashRecords(inputs))));
        _requireThreshold(digest, signatures);

        uint256 perEpoch = checkpointsPerEpoch();
        uint256 uptimeGap = config().getUint(TelemetryConfigKeys.DIVERGENCE_UPTIME_BPS);
        uint256 latencyGap = config().getUint(TelemetryConfigKeys.DIVERGENCE_LATENCY_BPS);
        TelemetryStorage storage $ = _ts();
        for (uint256 i; i < inputs.length; ++i) {
            AggregatedMetricInput calldata r = inputs[i];
            if ($.records[epoch][r.node].exists) revert RecordAlreadyWritten(epoch, r.node);
            if (
                r.node == address(0) || r.heartbeatsExpected == 0 || r.heartbeatsReceived > r.heartbeatsExpected
                    || r.firstCheckpoint > r.lastCheckpoint || r.lastCheckpoint >= perEpoch
                    || r.selfReported.uptimeBps > 10_000 || r.verifierAttested.uptimeBps > 10_000
            ) revert InvalidRecord(r.node);

            uint16 compliance = uint16((uint256(r.heartbeatsReceived) * 10_000) / r.heartbeatsExpected);
            bool eligible = r.heartbeatsReceived > 0;
            bool divergent = r.attestationCount > 0 && _diverges(r.selfReported, r.verifierAttested, uptimeGap, latencyGap);
            $.records[epoch][r.node] = AggregatedMetricRecord({
                selfReported: r.selfReported,
                verifierAttested: r.verifierAttested,
                heartbeatsReceived: r.heartbeatsReceived,
                heartbeatsExpected: r.heartbeatsExpected,
                heartbeatComplianceBps: compliance,
                attestationCount: r.attestationCount,
                firstCheckpoint: r.firstCheckpoint,
                lastCheckpoint: r.lastCheckpoint,
                eligible: eligible,
                divergenceFlag: divergent,
                exists: true
            });
            emit AggregatedRecordWritten(epoch, r.node, compliance, eligible, divergent);
        }
    }

    /// @notice Stable read interface for #76 (scoring), #79 (challenges) and
    /// #80 (distribution). `exists == false` means no record was written,
    /// which consumers must treat as ineligible.
    function getEpochMetricRecord(address node, uint256 epoch) external view returns (AggregatedMetricRecord memory) {
        return _ts().records[epoch][node];
    }

    /// @notice Hash the committee signs over a record batch (exposed so
    /// off-chain signers can cross-check their encoding).
    function hashRecords(AggregatedMetricInput[] calldata inputs) public pure returns (bytes32) {
        return keccak256(abi.encode(inputs));
    }

    function _diverges(MetricValues calldata s, MetricValues calldata v, uint256 uptimeGap, uint256 latencyGap)
        private
        pure
        returns (bool)
    {
        if (uptimeGap != 0) {
            uint256 gap = s.uptimeBps > v.uptimeBps ? s.uptimeBps - v.uptimeBps : v.uptimeBps - s.uptimeBps;
            if (gap > uptimeGap) return true;
        }
        if (latencyGap != 0 && v.latencyMs != 0 && s.latencyMs != 0) {
            uint256 gap = s.latencyMs > v.latencyMs ? s.latencyMs - v.latencyMs : v.latencyMs - s.latencyMs;
            if (gap * 10_000 > uint256(v.latencyMs) * latencyGap) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------
    // Epoch timing (same config keys and formula as RewardDistributor)
    // ---------------------------------------------------------------------

    function epochFinalityDeadline(uint256 epoch) public view returns (uint256) {
        IProtocolConfig cfg = config();
        return cfg.getUint(ConfigKeys.EPOCH_GENESIS_TIMESTAMP) + (epoch + 1) * cfg.getUint(ConfigKeys.EPOCH_LENGTH)
            + cfg.getUint(ConfigKeys.REWARD_FINALITY_WINDOW);
    }

    function checkpointsPerEpoch() public view returns (uint256) {
        uint256 n = config().getUint(TelemetryConfigKeys.CHECKPOINTS_PER_EPOCH);
        return n == 0 ? DEFAULT_CHECKPOINTS_PER_EPOCH : n;
    }

    /// @notice End timestamp of checkpoint window `index` within `epoch`.
    function checkpointWindowEnd(uint256 epoch, uint256 index) public view returns (uint256) {
        IProtocolConfig cfg = config();
        uint256 length = cfg.getUint(ConfigKeys.EPOCH_LENGTH);
        return cfg.getUint(ConfigKeys.EPOCH_GENESIS_TIMESTAMP) + epoch * length
            + ((index + 1) * length) / checkpointsPerEpoch();
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    function _checkIndex(uint256 index) private view {
        uint256 perEpoch = checkpointsPerEpoch();
        if (index >= perEpoch) revert CheckpointIndexOutOfRange(index, perEpoch);
    }

    function _requireThreshold(bytes32 digest, bytes[] calldata signatures) private view {
        TelemetryStorage storage $ = _ts();
        if ($.generation == 0) revert NoCommittee();
        uint256 gen = $.generation;
        address last;
        for (uint256 i; i < signatures.length; ++i) {
            address signer = ECDSA.recover(digest, signatures[i]);
            if (signer <= last) revert SignersNotSorted();
            if ($.memberGeneration[signer] != gen) revert NotCommitteeMember(signer);
            last = signer;
        }
        if (signatures.length < $.threshold) revert InsufficientSignatures(signatures.length, $.threshold);
    }

    function _ts() private pure returns (TelemetryStorage storage $) {
        bytes32 slot = TELEMETRY_STORAGE_LOCATION;
        assembly {
            $.slot := slot
        }
    }
}
