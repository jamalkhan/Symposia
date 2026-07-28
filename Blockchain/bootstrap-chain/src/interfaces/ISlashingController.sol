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

    /// @notice Issue #91: the compute-specific `ComputePenaltyStateMachine`
    /// runs its own telemetry-driven stage progression (distinct trigger
    /// table from the hardware-fault Stage 1-4 model this contract owns) and
    /// has already computed the exact `amount` to slash; this just reuses
    /// SlashingController's existing token-disposition plumbing rather than
    /// duplicating burn/redistribute logic in the new contract. Restricted
    /// to the single allowlisted `computePenalty.stateMachineAddress`.
    function applyComputePenaltySlash(address node, uint256 amount) external returns (uint256);
}
