// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title FactorScoring
/// @notice The one scoring pipeline every capacity node type shares (issue #76,
/// tokenomics-mvp.md §6.3): normalize each factor against the best node of the same
/// type in the same epoch, then take the weighted sum. Only the weights and the raw
/// inputs differ between types.
///
/// - Higher-is-better: `WAD * raw / max`.
/// - Lower-is-better (latency, TTFB): `WAD * min / raw`, where `min` is the smallest
///   non-zero value. A zero raw value means "not measured" and normalizes to 0.
/// - Score: `sum(normalized_i * weightBps_i) / 10_000`, in WAD (1e18 = 1.0).
///
/// Every division floors and factors are processed in table order, so the same inputs
/// always yield the same integer, on-chain or in any off-chain verifier. One node
/// leading a factor pins it to 1.0 and compresses its peers; that's the documented
/// relative-scoring behavior, not a bug. A lone node scores 1.0 on every factor it
/// reports.
library FactorScoring {
    uint256 internal constant WAD = 1e18;
    uint256 internal constant BPS = 10_000;

    /// @notice "No data" (#227): the factor is left out of this node's score and the remaining weights are scaled up to
    /// 100%, so missing data neither rewards nor punishes. Distinct from 0, which means "measured as nothing".
    uint256 internal constant NO_DATA = type(uint256).max;

    /// @notice Per-factor best values across `raws` (one row per node of a type).
    function bests(uint256[][] memory raws, bool[] memory lowerIsBetter) public pure returns (uint256[] memory best) {
        uint256 factors = lowerIsBetter.length;
        best = new uint256[](factors);
        for (uint256 n; n < raws.length; ++n) {
            for (uint256 f; f < factors; ++f) {
                uint256 v = raws[n][f];
                if (v == 0 || v == NO_DATA) continue;
                if (lowerIsBetter[f] ? (best[f] == 0 || v < best[f]) : v > best[f]) best[f] = v;
            }
        }
    }

    function normalize(uint256 raw, uint256 best, bool lowerIsBetter) internal pure returns (uint256) {
        if (raw == 0 || best == 0) return 0;
        return lowerIsBetter ? (WAD * best) / raw : (WAD * raw) / best;
    }

    /// @notice Weighted score of one node in WAD.
    function score(uint256[] memory raw, uint256[] memory best, uint16[] memory weightsBps, bool[] memory lowerIsBetter)
        public
        pure
        returns (uint256 total)
    {
        uint256 counted;
        for (uint256 f; f < weightsBps.length; ++f) {
            if (raw[f] == NO_DATA) continue;
            counted += weightsBps[f];
            total += (normalize(raw[f], best[f], lowerIsBetter[f]) * weightsBps[f]) / BPS;
        }
        if (counted != 0 && counted < BPS) total = (total * BPS) / counted;
    }
}
