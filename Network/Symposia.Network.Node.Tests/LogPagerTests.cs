namespace Symposia.Network.Node.Tests;

/// <summary>Issue #172: log scans page under a provider's eth_getLogs cap.</summary>
public sealed class LogPagerTests
{
    private static async Task<List<(ulong From, ulong To)>> Windows(ulong from, ulong to, ulong window) =>
        await LogPager.ScanAsync<(ulong, ulong)>(from, to, window, (f, t) => Task.FromResult<IEnumerable<(ulong, ulong)>>([(f, t)]));

    [Fact]
    public async Task SplitsIntoInclusiveWindowsNoWiderThanTheCap()
    {
        Assert.Equal([(0UL, 4_999UL), (5_000UL, 9_999UL), (10_000UL, 10_500UL)], await Windows(0, 10_500, 5_000));
    }

    [Fact]
    public async Task ExactMultiple_EndsOnTheLastBlock()
    {
        Assert.Equal([(10UL, 19UL), (20UL, 29UL)], await Windows(10, 29, 10));
    }

    [Fact]
    public async Task SingleBlock_IsOneWindow()
    {
        Assert.Equal([(7UL, 7UL)], await Windows(7, 7, 5_000));
    }

    [Fact]
    public async Task EmptyRange_FetchesNothing()
    {
        Assert.Empty(await Windows(10, 9, 5_000));
    }

    [Fact]
    public async Task RangeEndingAtMaxValue_Terminates()
    {
        Assert.Equal(2, (await Windows(ulong.MaxValue - 5, ulong.MaxValue, 3)).Count);
    }
}
