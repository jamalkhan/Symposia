// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {MerkleProof} from "@openzeppelin/contracts/utils/cryptography/MerkleProof.sol";
import {EIP712Upgradeable} from "@openzeppelin/contracts-upgradeable/utils/cryptography/EIP712Upgradeable.sol";
import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";
import {IEpochController} from "./interfaces/IEpochController.sol";

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
        // #76: storage capacity (median of the node's self-reported heartbeats), for the
        // available/used storage factors. Zero for non-storage nodes.
        uint64 availableBytes;
        uint64 usedBytes;
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
        uint64 availableBytes; // #76, appended for upgrade safety
        uint64 usedBytes;
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
        // #158: records are submitted in signed chunks; an epoch's records are final once all land.
        mapping(uint256 => uint256) recordChunkCount;
        mapping(uint256 => mapping(uint256 => bool)) recordChunkWritten;
        mapping(uint256 => uint256) recordChunksWritten;
        mapping(uint256 => uint64) recordsFinalizedAt;
        mapping(uint256 => uint256) recordCount; // #76: distinct nodes with a record per epoch
    }

    // keccak256(abi.encode(uint256(keccak256("symposia.TelemetryCheckpointRegistry")) - 1)) & ~bytes32(uint256(0xff))
    bytes32 private constant TELEMETRY_STORAGE_LOCATION =
        0xa65fed527fb0a7d4e7fe4a45377cc933d800a0aef8dbd2b95282e0d3eb3f6e00;

    bytes32 public constant CHECKPOINT_TYPEHASH = keccak256(
        "TelemetryCheckpoint(uint256 epochIndex,uint256 checkpointIndex,bytes32 merkleRoot,bytes32 blobAddress,uint32 sampleCount)"
    );
    bytes32 public constant AGGREGATE_TYPEHASH = keccak256(
        "AggregatedRecords(uint256 epochIndex,uint256 chunkIndex,uint256 chunkCount,bytes32 recordsHash)"
    );
    bytes32 public constant SELF_CHECKPOINT_TYPEHASH = keccak256(
        "SelfCheckpoint(address node,uint256 epochIndex,uint256 checkpointIndex,bytes32 merkleRoot,bytes32 blobAddress,uint32 sampleCount)"
    );

    uint256 internal constant DEFAULT_CHECKPOINTS_PER_EPOCH = 24;
    uint256 internal constant DEFAULT_COMMIT_WINDOW = 30 minutes;
    uint256 internal constant DEFAULT_RECORDS_PER_TX = 200;

    event CommitteeSet(uint256 indexed generation, address[] members, uint256 threshold);
    event CheckpointCommitted(
        uint256 indexed epoch, uint256 indexed checkpointIndex, bytes32 merkleRoot, bytes32 blobAddress, uint32 sampleCount
    );
    event SelfCheckpointCommitted(
        uint256 indexed epoch, uint256 indexed checkpointIndex, address indexed node, bytes32 merkleRoot, bytes32 blobAddress
    );
    event RecordsChunkWritten(uint256 indexed epoch, uint256 chunkIndex, uint256 chunkCount, uint256 records);
    event RecordsFinalized(uint256 indexed epoch);
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
    error TelemetryDeadlineElapsed(uint256 epoch);
    error TelemetryDeadlineNotElapsed(uint256 epoch);
    error InvalidChunk(uint256 chunkIndex, uint256 chunkCount);
    error ChunkAlreadyWritten(uint256 epoch, uint256 chunkIndex);
    error TooManyRecords(uint256 count, uint256 max);
    error InvalidNodeSignature(address node);
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
        if (block.timestamp > telemetryDeadline(epoch)) revert TelemetryDeadlineElapsed(epoch);
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
    /// `windowEnd + grace` until the epoch's telemetry deadline.
    function selfCheckpoint(
        uint256 epoch,
        uint256 checkpointIndex,
        bytes32 merkleRoot,
        bytes32 blobAddress,
        uint32 sampleCount
    ) external whenNotPaused {
        _selfCheckpoint(msg.sender, epoch, checkpointIndex, merkleRoot, blobAddress, sampleCount);
    }

    /// @notice `selfCheckpoint` relayed by someone else (the Gateway, which pays gas, #158),
    /// authorized by the node's EIP-712 signature. No nonce: a node can self-checkpoint each
    /// window at most once, and only until the telemetry deadline.
    function selfCheckpointFor(
        address node,
        uint256 epoch,
        uint256 checkpointIndex,
        bytes32 merkleRoot,
        bytes32 blobAddress,
        uint32 sampleCount,
        bytes calldata signature
    ) external whenNotPaused {
        bytes32 digest = _hashTypedDataV4(
            keccak256(
                abi.encode(SELF_CHECKPOINT_TYPEHASH, node, epoch, checkpointIndex, merkleRoot, blobAddress, sampleCount)
            )
        );
        (address signer, ECDSA.RecoverError err,) = ECDSA.tryRecover(digest, signature);
        if (err != ECDSA.RecoverError.NoError || signer != node) revert InvalidNodeSignature(node);
        _selfCheckpoint(node, epoch, checkpointIndex, merkleRoot, blobAddress, sampleCount);
    }

    function _selfCheckpoint(
        address node,
        uint256 epoch,
        uint256 checkpointIndex,
        bytes32 merkleRoot,
        bytes32 blobAddress,
        uint32 sampleCount
    ) private {
        _checkIndex(checkpointIndex);
        if (block.timestamp > telemetryDeadline(epoch)) revert TelemetryDeadlineElapsed(epoch);
        if (merkleRoot == bytes32(0) || sampleCount == 0) revert EmptyCheckpoint();
        TelemetryStorage storage $ = _ts();
        if ($.checkpoints[epoch][checkpointIndex].committedAt != 0) {
            revert CheckpointAlreadyCommitted(epoch, checkpointIndex);
        }
        uint256 grace = config().getUint(ConfigKeys.TELEMETRY_SELF_CHECKPOINT_GRACE);
        if (block.timestamp < checkpointWindowEnd(epoch, checkpointIndex) + grace) {
            revert SelfCheckpointNotYetAllowed(epoch, checkpointIndex);
        }
        INodeRegistry registry = INodeRegistry(config().getAddress(ConfigKeys.REGISTRY_ADDRESS));
        // Self-checkpoints cover storage heartbeats, so the Storage service must be Active (#159).
        if (
            address(registry) == address(0)
                || registry.serviceStatusOf(node, INodeRegistry.NodeType.Storage) != INodeRegistry.NodeStatus.Active
        ) {
            revert NotActiveNode(node);
        }
        if ($.selfCheckpoints[epoch][checkpointIndex][node].committedAt != 0) {
            revert CheckpointAlreadyCommitted(epoch, checkpointIndex);
        }

        $.selfCheckpoints[epoch][checkpointIndex][node] = TelemetryCheckpoint({
            merkleRoot: merkleRoot,
            blobAddress: blobAddress,
            sampleCount: sampleCount,
            committedAt: uint64(block.timestamp)
        });
        emit SelfCheckpointCommitted(epoch, checkpointIndex, node, merkleRoot, blobAddress);
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

    /// @notice Writes one committee-signed chunk of `epoch`'s aggregated records.
    /// Only after the telemetry deadline (so every window could be checkpointed).
    /// Members split the address-sorted record set into fixed `recordsPerTx()`
    /// slices; the signature covers the chunk's position and the total, so once
    /// every chunk has landed the epoch's records are final (`recordsFinalized`).
    /// An epoch with no records is finalized by one empty chunk (0 of 1).
    function submitAggregatedRecords(
        uint256 epoch,
        uint256 chunkIndex,
        uint256 chunkCount,
        AggregatedMetricInput[] calldata inputs,
        bytes[] calldata signatures
    ) external whenNotPaused {
        if (block.timestamp <= telemetryDeadline(epoch)) revert TelemetryDeadlineNotElapsed(epoch);
        if (chunkCount == 0 || chunkIndex >= chunkCount) revert InvalidChunk(chunkIndex, chunkCount);
        if (inputs.length > recordsPerTx()) revert TooManyRecords(inputs.length, recordsPerTx());
        bytes32 digest = _hashTypedDataV4(
            keccak256(abi.encode(AGGREGATE_TYPEHASH, epoch, chunkIndex, chunkCount, hashRecords(inputs)))
        );
        _requireThreshold(digest, signatures);
        _recordChunk(epoch, chunkIndex, chunkCount, inputs.length);

        uint256 perEpoch = checkpointsPerEpoch();
        uint256 uptimeGap = config().getUint(ConfigKeys.TELEMETRY_DIVERGENCE_UPTIME_BPS);
        uint256 latencyGap = config().getUint(ConfigKeys.TELEMETRY_DIVERGENCE_LATENCY_BPS);
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
                exists: true,
                availableBytes: r.availableBytes,
                usedBytes: r.usedBytes
            });
            $.recordCount[epoch] += 1;
            emit AggregatedRecordWritten(epoch, r.node, compliance, eligible, divergent);
        }
    }

    function _recordChunk(uint256 epoch, uint256 chunkIndex, uint256 chunkCount, uint256 records) private {
        TelemetryStorage storage $ = _ts();
        uint256 known = $.recordChunkCount[epoch];
        if (known == 0) $.recordChunkCount[epoch] = chunkCount;
        else if (known != chunkCount) revert InvalidChunk(chunkIndex, chunkCount);
        if ($.recordChunkWritten[epoch][chunkIndex]) revert ChunkAlreadyWritten(epoch, chunkIndex);
        $.recordChunkWritten[epoch][chunkIndex] = true;
        emit RecordsChunkWritten(epoch, chunkIndex, chunkCount, records);
        if (++$.recordChunksWritten[epoch] == chunkCount) {
            $.recordsFinalizedAt[epoch] = uint64(block.timestamp);
            emit RecordsFinalized(epoch);
        }
    }

    /// @notice True once every chunk of `epoch`'s records is on-chain (#76 gates settlement on this).
    function recordsFinalized(uint256 epoch) external view returns (bool) {
        return _ts().recordsFinalizedAt[epoch] != 0;
    }

    function recordsFinalizedAt(uint256 epoch) external view returns (uint256) {
        return _ts().recordsFinalizedAt[epoch];
    }

    /// @notice Distinct nodes with a record for `epoch`; settlement must cover all of them (#76).
    function recordCount(uint256 epoch) external view returns (uint256) {
        return _ts().recordCount[epoch];
    }

    /// @notice Chunk count fixed by `epoch`'s first chunk (0 = none yet); later chunks must match it.
    function recordChunkCount(uint256 epoch) external view returns (uint256) {
        return _ts().recordChunkCount[epoch];
    }

    function recordChunkWritten(uint256 epoch, uint256 chunkIndex) external view returns (bool) {
        return _ts().recordChunkWritten[epoch][chunkIndex];
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
    // Epoch timing (EpochController, #72)
    // ---------------------------------------------------------------------

    function epochFinalityDeadline(uint256 epoch) public view returns (uint256) {
        return _epochs().finalityDeadline(epoch);
    }

    /// @notice Last second (inclusive) at which checkpoints for `epoch` are accepted:
    /// the finality deadline plus `TELEMETRY_COMMIT_WINDOW` (default 30 min), so the
    /// final hourly window has time to be gossiped, endorsed and self-checkpointed (#158).
    /// Aggregated records are accepted only after it.
    function telemetryDeadline(uint256 epoch) public view returns (uint256) {
        uint256 window = config().getUint(ConfigKeys.TELEMETRY_COMMIT_WINDOW);
        return epochFinalityDeadline(epoch) + (window == 0 ? DEFAULT_COMMIT_WINDOW : window);
    }

    function recordsPerTx() public view returns (uint256) {
        uint256 n = config().getUint(ConfigKeys.TELEMETRY_RECORDS_PER_TX);
        return n == 0 ? DEFAULT_RECORDS_PER_TX : n;
    }

    function _epochs() internal view returns (IEpochController) {
        return IEpochController(config().getAddress(ConfigKeys.EPOCH_CONTROLLER));
    }

    function checkpointsPerEpoch() public view returns (uint256) {
        uint256 n = config().getUint(ConfigKeys.TELEMETRY_CHECKPOINTS_PER_EPOCH);
        return n == 0 ? DEFAULT_CHECKPOINTS_PER_EPOCH : n;
    }

    /// @notice End timestamp of checkpoint window `index` within `epoch`.
    function checkpointWindowEnd(uint256 epoch, uint256 index) public view returns (uint256) {
        IEpochController epochs = _epochs();
        uint256 start = epochs.epochStart(epoch);
        uint256 length = epochs.epochEnd(epoch) - start;
        return start + ((index + 1) * length) / checkpointsPerEpoch();
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
