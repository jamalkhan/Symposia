// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title BonusNormalization
/// @notice Peer-normalized reliability bonus within a type pool (issue #79,
/// tokenomics-mvp.md §6.4). The bonus is never inflationary: payouts always
/// sum to exactly `pool`, to the wei.
///
/// Normal case (Gherkin "bonus does not inflate the type pool"): each
/// bonus-eligible node gets exactly `base * bonusBps / 10_000`, and every
/// non-eligible node's base share is scaled down by the same factor so they
/// absorb the surplus.
///
/// Edge case: if non-eligible peers can't fully absorb the surplus (none
/// exist, or eligible nodes hold almost the whole pool), it falls back to
/// proportional weighting: `pool * w_i / sum(w)` with `w_i = base_i * bonus`
/// for eligible nodes and `base_i` otherwise. The bonus degrades gracefully
/// toward 1.0x as the eligible share nears 100% (e.g. every node eligible
/// -> everyone gets exactly their base share). This was flagged in the #79
/// Arch risks as the MVP default.
///
/// Rounding: all divisions floor, and the leftover dust goes to the
/// largest-weighted node in the set being scaled, so `sum(out) == pool`.
library BonusNormalization {
    uint256 internal constant BPS = 10_000;

    error LengthMismatch();
    error SharesExceedPool(uint256 sharesTotal, uint256 pool);

    /// @param pool Type pool total (what the outputs must sum to).
    /// @param base Pre-bonus score-proportional shares. Must sum to <= pool;
    /// any rounding gap is folded back in.
    /// @param eligible Per-node bonus eligibility.
    /// @param bonusBps Bonus multiplier in bps (10_500 = 1.05x).
    function normalize(uint256 pool, uint256[] memory base, bool[] memory eligible, uint256 bonusBps)
        internal
        pure
        returns (uint256[] memory out)
    {
        uint256 n = base.length;
        if (eligible.length != n) revert LengthMismatch();
        out = new uint256[](n);
        if (n == 0 || pool == 0) return out;

        uint256 baseTotal;
        uint256 nonEligibleTotal;
        uint256 surplus;
        for (uint256 i; i < n; ++i) {
            baseTotal += base[i];
            if (eligible[i]) surplus += (base[i] * bonusBps) / BPS - base[i];
            else nonEligibleTotal += base[i];
        }
        if (baseTotal > pool) revert SharesExceedPool(baseTotal, pool);
        if (baseTotal == 0) return out;

        if (bonusBps >= BPS && surplus < nonEligibleTotal) {
            // Closed form: eligible get exactly base*bonus; non-eligible
            // split what remains in proportion to their base shares.
            uint256 eligiblePaid;
            for (uint256 i; i < n; ++i) {
                if (eligible[i]) {
                    out[i] = (base[i] * bonusBps) / BPS;
                    eligiblePaid += out[i];
                }
            }
            uint256 remaining = pool - eligiblePaid;
            _proportional(remaining, base, eligible, false, out);
        } else {
            // Fallback: proportional over bonus-weighted shares.
            uint256[] memory w = new uint256[](n);
            for (uint256 i; i < n; ++i) {
                w[i] = eligible[i] ? base[i] * bonusBps : base[i] * BPS;
            }
            bool[] memory all = new bool[](n);
            _proportional(pool, w, all, false, out);
        }
    }

    /// @dev Splits `amount` across entries where `eligible[i] == match_`, in
    /// proportion to `weights`, flooring and handing the dust to the largest weight.
    function _proportional(
        uint256 amount,
        uint256[] memory weights,
        bool[] memory eligible,
        bool match_,
        uint256[] memory out
    ) private pure {
        uint256 n = weights.length;
        uint256 totalWeight;
        uint256 largest = type(uint256).max;
        for (uint256 i; i < n; ++i) {
            if (eligible[i] != match_ || weights[i] == 0) continue;
            totalWeight += weights[i];
            if (largest == type(uint256).max || weights[i] > weights[largest]) largest = i;
        }
        if (totalWeight == 0) return;
        uint256 paid;
        for (uint256 i; i < n; ++i) {
            if (eligible[i] != match_ || weights[i] == 0) continue;
            out[i] = (amount * weights[i]) / totalWeight;
            paid += out[i];
        }
        out[largest] += amount - paid;
    }
}
