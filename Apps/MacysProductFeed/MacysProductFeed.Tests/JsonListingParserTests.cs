using MacysProductFeed.Parsing;
using MacysProductFeed.Scraping;

namespace MacysProductFeed.Tests;

public class JsonListingParserTests
{
    private readonly JsonListingParser _parser = new();

    private static FetchedPage Fetched(string json) => new(json, "");

    [Fact]
    public void TryParse_ReturnsNull_WhenSourceIsNotJson()
    {
        var result = _parser.TryParse(Fetched("<html><body>not json</body></html>"), _ => { });

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_ReturnsNull_WhenNoProductArrayFound()
    {
        var result = _parser.TryParse(Fetched("""{"unrelated":"payload"}"""), _ => { });

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_ExtractsAllFields_ForCompleteProduct()
    {
        var json = """
        {
          "products": [
            {
              "productId": "P123",
              "title": "Women's Cotton Top",
              "brand": "Style&Co",
              "regularPrice": 49.99,
              "salePrice": 34.99,
              "productUrl": "https://www.macys.com/shop/product/p123",
              "imageUrl": "https://slimages.macysassets.com/p123.jpg",
              "availability": "In Stock",
              "category": "Womens/Tops"
            }
          ],
          "hasNextPage": true
        }
        """;

        var result = _parser.TryParse(Fetched(json), _ => { });

        Assert.NotNull(result);
        Assert.True(result!.HasNextPage);
        var product = Assert.Single(result.Products);
        Assert.Equal("P123", product.ProductId);
        Assert.Equal("Women's Cotton Top", product.Title);
        Assert.Equal("Style&Co", product.Brand);
        Assert.Equal(49.99m, product.RegularPrice);
        Assert.Equal(34.99m, product.SalePrice);
        Assert.Equal("USD", product.Currency);
        Assert.Equal("https://www.macys.com/shop/product/p123", product.ProductUrl);
        Assert.Equal("https://slimages.macysassets.com/p123.jpg", product.ImageUrl);
        Assert.Equal("In Stock", product.Availability);
        Assert.Equal("Womens/Tops", product.Category);
    }

    [Fact]
    public void TryParse_LeavesSalePriceNull_WhenProductNotOnSale()
    {
        var json = """{"products":[{"productId":"P1","regularPrice":20.00}]}""";

        var result = _parser.TryParse(Fetched(json), _ => { });

        Assert.Null(Assert.Single(result!.Products).SalePrice);
    }

    [Fact]
    public void TryParse_SkipsEntry_AndReportsTileError_WhenProductIdMissing()
    {
        var json = """{"products":[{"title":"No ID here"},{"productId":"P2","title":"Valid"}]}""";
        var errors = new List<string>();

        var result = _parser.TryParse(Fetched(json), errors.Add);

        Assert.Single(result!.Products);
        Assert.Equal("P2", result.Products[0].ProductId);
        Assert.Single(errors);
    }

    [Fact]
    public void TryParse_UsesFallbackPropertyNames()
    {
        var json = """{"items":[{"id":"P3","name":"Fallback Name","brandName":"Fallback Brand","price":15.5}]}""";

        var result = _parser.TryParse(Fetched(json), _ => { });

        var product = Assert.Single(result!.Products);
        Assert.Equal("P3", product.ProductId);
        Assert.Equal("Fallback Name", product.Title);
        Assert.Equal("Fallback Brand", product.Brand);
        Assert.Equal(15.5m, product.RegularPrice);
    }

    [Fact]
    public void TryParse_DefaultsHasNextPageToFalse_WhenAbsent()
    {
        var json = """{"products":[{"productId":"P4"}]}""";

        var result = _parser.TryParse(Fetched(json), _ => { });

        Assert.False(result!.HasNextPage);
    }
}
