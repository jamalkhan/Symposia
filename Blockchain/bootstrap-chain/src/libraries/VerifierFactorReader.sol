// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {VerifierAssignments} from "../VerifierAssignments.sol";
import {IProtocolConfig} from "../config/IProtocolConfig.sol";
import {ConfigKeys} from "../config/ConfigKeys.sol";

/// @title VerifierFactorReader
/// @notice Verifier factors for RewardDistributor (#227), as an external library for EIP-170 headroom like
/// TelemetryRecordReader: uptime from the node's telemetry heartbeat record, coverage and accuracy from
/// VerifierAssignments. Once assignments are configured these replace anything a signed report stored for a
/// Verifier at seal, so the metric signer can't override peer results.
library VerifierFactorReader {
    error VerifierFinalizePending(uint256 epoch);

    function assignmentsOf(address config) internal view returns (address) {
        return IProtocolConfig(config).getAddress(ConfigKeys.REWARD_VERIFIER_ASSIGNMENTS);
    }

    /// @notice Whether Verifiers are scored on-chain, and if so the ScoringWeights table-5 factors
    /// [uptime, coverage, accuracy]; empty (Ineligible) without a telemetry record.
    function read(address config, address node, uint256 epoch, bool hasRecord, uint256 uptimeBps)
        public
        view
        returns (bool onChain, uint256[] memory raw)
    {
        address assignments = assignmentsOf(config);
        onChain = assignments != address(0);
        if (!onChain || !hasRecord) return (onChain, raw);
        (uint256 coverage, uint256 accuracy) = VerifierAssignments(assignments).factorsOf(epoch, node);
        raw = new uint256[](3);
        (raw[0], raw[1], raw[2]) = (uptimeBps, coverage, accuracy);
    }

    /// @notice Seal waits for `finalize` when Verifier assignments are configured.
    function requireFinalized(address config, uint256 epoch) public view {
        address assignments = assignmentsOf(config);
        if (assignments != address(0) && !VerifierAssignments(assignments).isFinalized(epoch)) revert VerifierFinalizePending(epoch);
    }
}
