// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title ISlashingController
/// @notice Narrow interface StakingNodeRegistry calls into to report a
/// detected overcommitment invariant violation (FR-2's "overcommitment ...
/// triggers the appropriate slashing hook"). SlashingController implements
/// this and applies the configured non-hardware-violation penalty.
interface ISlashingController {
    /// @notice Violation categories that bypass the progressive Stage 1-4
    /// model entirely (FR-5). Values are stable and additive only.
    enum ViolationType {
        Overcommitment,
        RegionVerificationFraud,
        RepeatedVerificationFailure,
        /// @notice Issue #57: a foundation node deregistered/powered down
        /// before its 12-month operational floor elapsed. Its own category
        /// (not overloaded onto the hardware-fault Stage 1-4 table or the
        /// other non-hardware violation types above), with its own
        /// config-supplied penalty percentage via `slashingViolationPctBps`.
        StakeCommitmentViolation
    }

    function reportNonHardwareViolation(address node, ViolationType violationType) external;
}
