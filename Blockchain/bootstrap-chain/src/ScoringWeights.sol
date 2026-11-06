// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {GovernedUpgradeable} from "./governance/GovernedUpgradeable.sol";
import {IProtocolConfig} from "./config/IProtocolConfig.sol";
import {ConfigKeys} from "./config/ConfigKeys.sol";
import {IEpochController} from "./interfaces/IEpochController.sol";
import {INodeRegistry} from "./interfaces/INodeRegistry.sol";

/// @title ScoringWeights
/// @notice Type-specific factor weight tables for epoch scoring (issue #76,
/// tokenomics-mvp.md §6.3). Factor order and direction are protocol constants per
/// type; governance may change only the weights. A change takes effect at the next
/// epoch, and the history is append-only, so a sealed epoch is always scored with the
/// table that was in force for it.
///
/// Factor order per node type (L = lower is better):
/// - Storage (0): retrieval time L, uptime, latency/TTFB L, I/O throughput, bandwidth,
///   available storage, used storage
/// - OLTP (1): uptime, query latency L, commit success, CPU reliability, connection capacity
/// - Analytics (2): uptime, query latency L, bytes-scanned efficiency, memory reliability, availability
/// - Consensus (3): uptime, participation, propagation latency L, correctness
/// - Serverless (4): accept uptime, invoke success, p95 invoke latency L, sandbox health, throughput
/// - Verifier (5, #81): uptime, coverage (assigned checks completed), accuracy (attestations upheld)
/// - Blob Gateway (8, #160 strawman): uptime, request success rate, p95 latency L, bytes served
contract ScoringWeights is GovernedUpgradeable {
    /// @notice Number of capacity tables seeded at the first deploy (ordinals 0-4); later
    /// Node Service tables (Verifier, Blob Gateway) are named below. Kept for ABI compatibility.
    uint8 public constant TYPE_COUNT = 5;
    uint8 internal constant VERIFIER = uint8(INodeRegistry.NodeType.Verifier);
    uint8 internal constant BLOB_GATEWAY = uint8(INodeRegistry.NodeType.BlobGateway);
    uint256 internal constant BPS = 10_000;

    struct Table {
        uint64 effectiveEpoch;
        uint16[] weightsBps;
    }

    /// @custom:storage-location erc7201:symposia.ScoringWeights
    struct ScoringStorage {
        mapping(uint8 => Table[]) history;
    }

    // keccak256(abi.encode(uint256(keccak256("symposia.ScoringWeights")) - 1)) & ~bytes32(uint256(0xff))
    bytes32 private constant SCORING_STORAGE_LOCATION =
        0xda8f36459100d4361670ed13a354f6f798c61bc0368e76bd36a357bf11af3200;

    event WeightsSet(uint8 indexed nodeType, uint256 indexed effectiveEpoch, uint16[] weightsBps);

    error UnknownNodeType(uint8 nodeType);
    error InvalidWeights(uint8 nodeType);

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        _disableInitializers();
    }

    /// @notice Seeds the locked §6.3 tables, effective from epoch 0.
    function initialize(IProtocolConfig initialConfig) external initializer {
        __GovernedUpgradeable_init(initialConfig);
        _push(0, 0, _u16([uint256(3_000), 2_000, 1_500, 1_000, 1_000, 800, 700]));
        _push(1, 0, _u16([uint256(2_500), 2_500, 2_000, 1_500, 1_500]));
        _push(2, 0, _u16([uint256(2_000), 2_500, 2_000, 2_000, 1_500]));
        _push(3, 0, _u16([uint256(3_500), 3_000, 2_000, 1_500]));
        _push(4, 0, _u16([uint256(2_000), 2_500, 2_000, 1_500, 2_000]));
        _seedNodeServiceTables();
    }

    /// @notice Adds the Node Service tables introduced after the first deploy (#81/#159)
    /// to a proxy initialized before them. No-op for tables that already exist.
    function initializeNodeServiceTables() external reinitializer(2) {
        _seedNodeServiceTables();
    }

    function _seedNodeServiceTables() private {
        if (_s().history[VERIFIER].length == 0) _push(VERIFIER, 0, _u16([uint256(4_000), 3_000, 3_000])); // Verifier
        if (_s().history[BLOB_GATEWAY].length == 0) _push(BLOB_GATEWAY, 0, _u16([uint256(3_000), 3_000, 2_000, 2_000])); // Blob Gateway
    }

    /// @notice Tier 3: new weights for `nodeType`, effective from the next epoch. Calling
    /// again before then replaces the pending table.
    function setWeights(uint8 nodeType, uint16[] calldata weightsBps) external onlyTimelock {
        uint256 next = IEpochController(config().getAddress(ConfigKeys.EPOCH_CONTROLLER)).currentEpoch() + 1;
        _push(nodeType, uint64(next), weightsBps);
    }

    function factorCount(uint8 nodeType) public pure returns (uint256) {
        if (nodeType == 0) return 7;
        if (nodeType == 3) return 4;
        if (nodeType < TYPE_COUNT) return 5;
        if (nodeType == VERIFIER) return 3;
        if (nodeType == BLOB_GATEWAY) return 4;
        revert UnknownNodeType(nodeType);
    }

    /// @notice Which factors of `nodeType` are lower-is-better, in table order.
    function directions(uint8 nodeType) public pure returns (bool[] memory lower) {
        lower = new bool[](factorCount(nodeType));
        if (nodeType == 0) {
            lower[0] = true; // retrieval time
            lower[2] = true; // latency / TTFB
        } else if (nodeType == 1 || nodeType == 2) {
            lower[1] = true; // query latency
        } else if (nodeType == 3) {
            lower[2] = true; // propagation latency
        } else if (nodeType == 4) {
            lower[2] = true; // p95 invoke latency
        } else if (nodeType == BLOB_GATEWAY) {
            lower[2] = true; // p95 request latency
        }
        // Verifier (5): every factor is higher-is-better.
    }

    /// @notice The weights in force for `nodeType` in `epoch`, with factor directions.
    function weightsFor(uint8 nodeType, uint256 epoch) external view returns (uint16[] memory weightsBps, bool[] memory lower) {
        Table[] storage h = _s().history[nodeType];
        if (h.length == 0) revert UnknownNodeType(nodeType);
        uint256 i = h.length;
        while (i > 1 && h[i - 1].effectiveEpoch > epoch) --i;
        return (h[i - 1].weightsBps, directions(nodeType));
    }

    function historyLength(uint8 nodeType) external view returns (uint256) {
        return _s().history[nodeType].length;
    }

    function _push(uint8 nodeType, uint64 effectiveEpoch, uint16[] memory weightsBps) private {
        if (weightsBps.length != factorCount(nodeType)) revert InvalidWeights(nodeType);
        uint256 sum;
        for (uint256 i; i < weightsBps.length; ++i) sum += weightsBps[i];
        if (sum != BPS) revert InvalidWeights(nodeType);

        Table[] storage h = _s().history[nodeType];
        if (h.length > 0 && h[h.length - 1].effectiveEpoch == effectiveEpoch) {
            h[h.length - 1].weightsBps = weightsBps; // pending, not yet in force
        } else {
            h.push(Table(effectiveEpoch, weightsBps));
        }
        emit WeightsSet(nodeType, effectiveEpoch, weightsBps);
    }

    function _u16(uint256[7] memory w) private pure returns (uint16[] memory out) {
        out = new uint16[](7);
        for (uint256 i; i < 7; ++i) out[i] = uint16(w[i]);
    }

    function _u16(uint256[5] memory w) private pure returns (uint16[] memory out) {
        out = new uint16[](5);
        for (uint256 i; i < 5; ++i) out[i] = uint16(w[i]);
    }

    function _u16(uint256[3] memory w) private pure returns (uint16[] memory out) {
        out = new uint16[](3);
        for (uint256 i; i < 3; ++i) out[i] = uint16(w[i]);
    }

    function _u16(uint256[4] memory w) private pure returns (uint16[] memory out) {
        out = new uint16[](4);
        for (uint256 i; i < 4; ++i) out[i] = uint16(w[i]);
    }

    function _s() private pure returns (ScoringStorage storage $) {
        bytes32 slot = SCORING_STORAGE_LOCATION;
        assembly {
            $.slot := slot
        }
    }
}
