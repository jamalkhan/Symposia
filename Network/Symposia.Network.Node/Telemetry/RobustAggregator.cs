namespace Symposia.Network.Node.Telemetry;

/// <summary>
/// Deterministic outlier rejection (issue #75 arch): median + MAD with the Iglewicz–Hoaglin
/// modified z-score. A sample is rejected when <c>|0.6745 · (x − median) / MAD| &gt; 3.5</c>.
/// That inequality is evaluated entirely in integers as
/// <c>6745 · |x − median| &gt; 35000 · MAD</c> (both sides scaled by 10⁴), so there is no
/// floating point anywhere and the same input always yields bit-identical output on every
/// platform and language. Medians of even-length sets average the two middle values with
/// integer division truncated toward zero.
/// </summary>
public static class RobustAggregator
{
    /// <summary>Below this many samples there is too little signal to call anything an outlier.</summary>
    public const int MinSamplesForRejection = 3;

    private const long ZScale = 6745;      // 0.6745 × 10⁴
    private const long Threshold = 35000;  // 3.5 × 10⁴

    /// <summary>Median of <paramref name="values"/> (order-independent); 0 for an empty set.</summary>
    public static ulong Median(IReadOnlyList<ulong> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        if (sorted.Length % 2 == 1)
        {
            return sorted[mid];
        }

        // Overflow-safe (a + b) / 2, truncated.
        var a = sorted[mid - 1];
        var b = sorted[mid];
        return a / 2 + b / 2 + (a % 2 + b % 2) / 2;
    }

    /// <summary>
    /// Values surviving outlier rejection, in ascending order. Rejection is skipped when there
    /// are fewer than <see cref="MinSamplesForRejection"/> samples or MAD is zero.
    /// </summary>
    public static IReadOnlyList<ulong> RejectOutliers(IReadOnlyList<ulong> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length < MinSamplesForRejection)
        {
            return sorted;
        }

        var median = Median(sorted);
        var deviations = sorted.Select(v => AbsDiff(v, median)).ToArray();
        var mad = Median(deviations);
        if (mad == 0)
        {
            return sorted;
        }

        var limit = (UInt128)Threshold * mad;
        return sorted.Where(v => (UInt128)ZScale * AbsDiff(v, median) <= limit).ToArray();
    }

    /// <summary>The published per-field aggregate: median of the outlier-rejected set.</summary>
    public static ulong Aggregate(IReadOnlyList<ulong> values) => Median(RejectOutliers(values));

    private static ulong AbsDiff(ulong a, ulong b) => a > b ? a - b : b - a;
}
