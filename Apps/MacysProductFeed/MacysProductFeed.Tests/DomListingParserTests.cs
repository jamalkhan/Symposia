using MacysProductFeed.Parsing;

namespace MacysProductFeed.Tests;

public class DomListingParserTests
{
    private readonly DomListingParser _parser = new();

    private static string Tile(
        string productId = "P1",
        string title = "Sample Product",
        string brand = "Sample Brand",
        string regularPrice = "$49.99",
        string? salePrice = null,
        string href = "/shop/product/p1",
        string imgSrc = "https://slimages.macysassets.com/p1.jpg",
        string? availability = "In Stock") => $"""
        <div data-testid="product-tile">
          <div data-product-id="{productId}"></div>
          <span class="productName">{title}</span>
          <span class="productBrand">{brand}</span>
          <span class="regular">{regularPrice}</span>
          {(salePrice is not null ? $"<span class=\"sale\">{salePrice}</span>" : "")}
          <a class="productDescLink" href="{href}"></a>
          <img class="productThumbnailImage" src="{imgSrc}" />
          {(availability is not null ? $"<span data-testid=\"product-availability\">{availability}</span>" : "")}
        </div>
        """;

    private static string Page(string body, bool hasNextPage = false) => $"""
        <html><body>
          {body}
          {(hasNextPage ? "<a rel=\"next\" href=\"?page=2\">Next</a>" : "")}
        </body></html>
        """;

    [Fact]
    public void TryParse_ReturnsNull_WhenNoTilesFound()
    {
        var result = _parser.TryParse(Page("<div>no products here</div>"), _ => { });

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_ExtractsAllFields_ForCompleteTile()
    {
        var html = Page(Tile(salePrice: "$34.99"), hasNextPage: true);

        var result = _parser.TryParse(html, _ => { });

        Assert.NotNull(result);
        Assert.True(result!.HasNextPage);
        var product = Assert.Single(result.Products);
        Assert.Equal("P1", product.ProductId);
        Assert.Equal("Sample Product", product.Title);
        Assert.Equal("Sample Brand", product.Brand);
        Assert.Equal(49.99m, product.RegularPrice);
        Assert.Equal(34.99m, product.SalePrice);
        Assert.Equal("USD", product.Currency);
        Assert.Equal("/shop/product/p1", product.ProductUrl);
        Assert.Equal("https://slimages.macysassets.com/p1.jpg", product.ImageUrl);
        Assert.Equal("In Stock", product.Availability);
    }

    [Fact]
    public void TryParse_LeavesSalePriceNull_WhenAbsent()
    {
        var html = Page(Tile(salePrice: null));

        var result = _parser.TryParse(html, _ => { });

        Assert.Null(Assert.Single(result!.Products).SalePrice);
    }

    [Fact]
    public void TryParse_LeavesAvailabilityNull_WhenNotDerivable()
    {
        var html = Page(Tile(availability: null));

        var result = _parser.TryParse(html, _ => { });

        Assert.Null(Assert.Single(result!.Products).Availability);
    }

    [Fact]
    public void TryParse_SkipsTile_AndReportsError_WhenProductIdMissing()
    {
        var brokenTile = """<div data-testid="product-tile"><span class="productName">No ID</span></div>""";
        var html = Page(brokenTile + Tile(productId: "P2"));
        var errors = new List<string>();

        var result = _parser.TryParse(html, errors.Add);

        Assert.Single(result!.Products);
        Assert.Equal("P2", result.Products[0].ProductId);
        Assert.Single(errors);
    }

    [Fact]
    public void TryParse_NoNextPage_WhenNextLinkAbsent()
    {
        var result = _parser.TryParse(Page(Tile(), hasNextPage: false), _ => { });

        Assert.False(result!.HasNextPage);
    }
}
