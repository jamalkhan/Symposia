using MacysProductFeed.Pipeline;

namespace MacysProductFeed.Tests;

public class RunSummaryTests
{
    [Fact]
    public void ExitCode_IsZero_WhenAtLeastOneProductWritten()
    {
        var summary = new RunSummary();
        summary.RecordProductsWritten(1);
        summary.RecordPageError();

        Assert.Equal(0, summary.ExitCode);
    }

    [Fact]
    public void ExitCode_IsNonZero_WhenNoProductsWritten()
    {
        var summary = new RunSummary();
        summary.RecordPageError();

        Assert.NotEqual(0, summary.ExitCode);
    }

    [Fact]
    public void ToString_ReflectsAllRecordedCounters()
    {
        var summary = new RunSummary();
        summary.RecordProductsWritten(5);
        summary.RecordPageProcessed();
        summary.RecordPageProcessed();
        summary.RecordPageError();
        summary.RecordTileError();
        summary.RecordDuplicatesSkipped(2);

        var text = summary.ToString();

        Assert.Contains("5 product(s) written", text);
        Assert.Contains("2 page(s) processed", text);
        Assert.Contains("1 page error(s)", text);
        Assert.Contains("1 tile error(s)", text);
        Assert.Contains("2 duplicate(s) skipped", text);
    }
}
