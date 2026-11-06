// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {TelemetryCheckpointRegistry} from "../TelemetryCheckpointRegistry.sol";

/// @title TelemetryRecordReader
/// @notice Reads a node's finalized committee record and maps it to the Storage factor
/// vector (#76). An external library so the large record decoding lives outside
/// RewardDistributor's bytecode (EIP-170 headroom, #165/#166).
library TelemetryRecordReader {
    /// @notice A record reduced to what settlement needs.
    struct Summary {
        bool exists;
        bool eligible;
        uint256 heartbeatBps;
        uint256[] raw; // Storage factors in ScoringWeights table-0 order
    }

    function read(address telemetry, address node, uint256 epoch) public view returns (Summary memory s) {
        TelemetryCheckpointRegistry.AggregatedMetricRecord memory r =
            TelemetryCheckpointRegistry(telemetry).getEpochMetricRecord(node, epoch);
        if (!r.exists) return s;
        s.exists = true;
        s.eligible = r.eligible;
        s.heartbeatBps = r.heartbeatComplianceBps;
        // Verifier-attested latency/retrieval when a verifier measured them, else self-reported.
        bool attested = r.attestationCount > 0;
        s.raw = new uint256[](7);
        s.raw[0] = attested && r.verifierAttested.retrievalMs != 0 ? r.verifierAttested.retrievalMs : r.selfReported.retrievalMs;
        s.raw[1] = r.heartbeatComplianceBps;
        s.raw[2] = attested && r.verifierAttested.latencyMs != 0 ? r.verifierAttested.latencyMs : r.selfReported.latencyMs;
        s.raw[3] = uint256(r.selfReported.readThroughputKBps) + r.selfReported.writeThroughputKBps;
        s.raw[4] = uint256(r.selfReported.bandwidthInKbps) + r.selfReported.bandwidthOutKbps;
        s.raw[5] = r.availableBytes;
        s.raw[6] = r.usedBytes;
    }
}
