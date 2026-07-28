// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title RegionDistribution
/// @notice Shared helper enforcing the "no more than X% of an active
/// verifier/node pool in a single region" constraint (`verifier-nodes.md`),
/// factored out (issue #57 Arch pass) so `FoundationRegistry` and the
/// general verifier-admission path (a later issue, per §3.3) can both
/// depend on the same logic rather than maintaining separate copies that
/// could drift out of sync as the pool grows past foundation-only
/// membership.
library RegionDistribution {
    error RegionCapExceeded(bytes32 region, uint256 regionCountAfter, uint256 totalAfter, uint256 capBps);

    /// @notice Reverts if adding one more member in `region` would push that
    /// region's share of the pool (after the addition) above `capBps`
    /// (basis points, e.g. 3_000 == 30%). `regionCount` / `totalCount` are
    /// the pool's counts *before* this addition. A `capBps` of 0 is treated
    /// as "not yet configured" and disables enforcement, rather than
    /// silently forbidding every registration.
    function checkCapOnAdd(bytes32 region, uint256 regionCount, uint256 totalCount, uint256 capBps) internal pure {
        if (capBps == 0) return;

        // A brand-new region (this is the first member ever registered in
        // it) can never itself be "over-concentrated" — concentration is a
        // property of a region that already has members. Without this
        // carve-out, a strict `share > capBps` reading would make it
        // mathematically impossible to found the very first handful of
        // foundation nodes at all (e.g. 3 nodes in 3 distinct regions is
        // 33% each, already over a 30% cap), which cannot be the intent of
        // a rule whose own spec requires >=3 regions to bootstrap. Adding a
        // *second* member to an already-represented region is still fully
        // subject to the cap below.
        if (regionCount == 0) return;

        uint256 regionAfter = regionCount + 1;
        uint256 totalAfter = totalCount + 1;
        if (regionAfter * 10_000 > totalAfter * capBps) {
            revert RegionCapExceeded(region, regionAfter, totalAfter, capBps);
        }
    }
}
