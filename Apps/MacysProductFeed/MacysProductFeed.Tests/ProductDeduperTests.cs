using MacysProductFeed.Parsing;
using MacysProductFeed.Pipeline;

namespace MacysProductFeed.Tests;

public class ProductDeduperTests
{
    private static ProductRecord Product(string id, string? title = null) =>
        new(id, title, null, null, null, "USD", null, null, null, null);

    [Fact]
    public void TryAdd_ReturnsTrue_ForNewProduct()
    {
        var deduper = new ProductDeduper();

        Assert.True(deduper.TryAdd(Product("P1")));
        Assert.Single(deduper.Records);
    }

    [Fact]
    public void TryAdd_ReturnsFalse_ForDuplicateProductId()
    {
        var deduper = new ProductDeduper();
        deduper.TryAdd(Product("P1", "First seen"));

        var result = deduper.TryAdd(Product("P1", "Second seen"));

        Assert.False(result);
        Assert.Equal(1, deduper.DuplicateCount);
    }

    [Fact]
    public void TryAdd_KeepsFirstSeenRecord_OnDuplicate()
    {
        var deduper = new ProductDeduper();
        deduper.TryAdd(Product("P1", "First seen"));
        deduper.TryAdd(Product("P1", "Second seen"));

        var record = Assert.Single(deduper.Records);
        Assert.Equal("First seen", record.Title);
    }

    [Fact]
    public void Records_ContainsUnionAcrossMultipleSources()
    {
        var deduper = new ProductDeduper();
        deduper.TryAdd(Product("P1"));
        deduper.TryAdd(Product("P2"));
        deduper.TryAdd(Product("P1")); // duplicate from a second URL

        Assert.Equal(2, deduper.Records.Count);
        Assert.Equal(1, deduper.DuplicateCount);
    }
}
