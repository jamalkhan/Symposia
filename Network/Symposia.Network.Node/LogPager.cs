namespace Symposia.Network.Node;

/// <summary>
/// Splits a block range into windows no wider than a provider's <c>eth_getLogs</c> cap (issue #172),
/// so log scans keep working through capped RPCs such as a gateway's read-only proxy.
/// </summary>
public static class LogPager
{
    public const ulong DefaultWindow = 5_000;

    /// <summary>Calls <paramref name="fetch"/> for each inclusive window of [<paramref name="from"/>, <paramref name="to"/>] in order.</summary>
    public static async Task<List<T>> ScanAsync<T>(ulong from, ulong to, ulong window, Func<ulong, ulong, Task<IEnumerable<T>>> fetch)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        var results = new List<T>();
        for (var start = from; start <= to;)
        {
            var end = to - start < window ? to : start + window - 1;
            results.AddRange(await fetch(start, end));
            if (end == ulong.MaxValue)
            {
                break;
            }

            start = end + 1;
        }

        return results;
    }
}
