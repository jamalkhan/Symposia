// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title IFoundationRewardHook
/// @notice Narrow interface `RewardDistributor` (issue #53) calls into, if
/// configured, at each point it is about to move payout tokens to a node
/// (issue #57 §6). Kept separate from `FoundationRegistry`'s full ABI so
/// `RewardDistributor` does not need to depend on foundation-specific types
/// to wrap its payout step, per the Arch pass's "post-calculation hook, not
/// a special-cased formula" guidance.
interface IFoundationRewardHook {
    /// @notice Given a node and the gross epoch reward amount already
    /// computed by the (unmodified) reward engine, returns how that amount
    /// should be split. For a non-foundation node this MUST return
    /// `(grossAmount, 0, address(0))` (no-op passthrough) so community-node
    /// payouts are unaffected. For a foundation node earning more than its
    /// configured operational cost baseline `X`, this returns
    /// `(min(grossAmount, X), grossAmount - min(grossAmount, X), ecosystemReserveAddress)`.
    function routeFoundationPayout(address node, uint256 epoch, uint256 grossAmount)
        external
        view
        returns (uint256 operatorAmount, uint256 reserveAmount, address reserveRecipient);
}
