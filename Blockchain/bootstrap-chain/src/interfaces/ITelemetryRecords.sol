// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {TelemetryCheckpointRegistry} from "../TelemetryCheckpointRegistry.sol";

/// @notice What settlement (#76/#80) reads from TelemetryCheckpointRegistry.
interface ITelemetryRecords {
    function getEpochMetricRecord(address node, uint256 epoch)
        external
        view
        returns (TelemetryCheckpointRegistry.AggregatedMetricRecord memory);
    function recordsFinalized(uint256 epoch) external view returns (bool);
    function recordCount(uint256 epoch) external view returns (uint256);
    function telemetryDeadline(uint256 epoch) external view returns (uint256);
}

/// @notice Type weight tables (ScoringWeights, #76).
interface IScoringWeights {
    function factorCount(uint8 nodeType) external pure returns (uint256);
    function weightsFor(uint8 nodeType, uint256 epoch) external view returns (uint16[] memory weightsBps, bool[] memory lower);
}
