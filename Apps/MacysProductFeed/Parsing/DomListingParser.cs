using AngleSharp;
using AngleSharp.Dom;

namespace MacysProductFeed.Parsing;

/// <summary>
/// Fallback parser: reads product tiles out of the rendered DOM when no
/// usable JSON payload was captured for a page. Selectors are centralized
/// in <see cref="Selectors"/> so a markup change only requires updating this
/// file, not the orchestrator/CLI/CSV layers (per the Arch plan's
/// maintainability notes).
/// </summary>
public sealed class DomListingParser : IListingPageParser
{
    public static class Selectors
    {
        public const string ProductTile = "[data-testid='product-tile'], .productThumbnail";
        public const string ProductId = "[data-product-id]";
        public const string Title = ".productName, [data-testid='product-name']";
        public const string Brand = ".productBrand, [data-testid='product-brand']";
        public const string RegularPrice = ".regular, [data-testid='regular-price']";
        public const string SalePrice = ".sale, [data-testid='sale-price']";
        public const string ProductLink = "a.productDescLink, a[data-testid='product-link']";
        public const string Image = "img.productThumbnailImage, img[data-testid='product-image']";
        public const string Availability = "[data-testid='product-availability']";
        public const string NextPageLink = "a[rel='next'], [data-testid='pagination-next']:not([aria-disabled='true'])";
    }

    private static readonly IBrowsingContext Context = BrowsingContext.New(Configuration.Default);

    public ParsedPage? TryParse(string source, Action<string> onTileError)
    {
        IDocument document = Context.OpenAsync(req => req.Content(source)).GetAwaiter().GetResult();
        var tiles = document.QuerySelectorAll(Selectors.ProductTile);
        if (tiles.Length == 0)
        {
            return null;
        }

        var products = new List<ProductRecord>(tiles.Length);
        foreach (var tile in tiles)
        {
            try
            {
                var productId = tile.QuerySelector(Selectors.ProductId)?.GetAttribute("data-product-id");
                if (string.IsNullOrWhiteSpace(productId))
                {
                    onTileError("Product tile missing product ID; skipped.");
                    continue;
                }

                products.Add(new ProductRecord(
                    ProductId: productId,
                    Title: TextOrNull(tile, Selectors.Title),
                    Brand: TextOrNull(tile, Selectors.Brand),
                    RegularPrice: PriceOrNull(tile, Selectors.RegularPrice),
                    SalePrice: PriceOrNull(tile, Selectors.SalePrice),
                    Currency: "USD",
                    ProductUrl: tile.QuerySelector(Selectors.ProductLink)?.GetAttribute("href"),
                    ImageUrl: tile.QuerySelector(Selectors.Image)?.GetAttribute("src"),
                    Availability: TextOrNull(tile, Selectors.Availability),
                    Category: null));
            }
            catch (Exception ex)
            {
                onTileError($"Failed to parse product tile: {ex.Message}");
            }
        }

        bool hasNextPage = document.QuerySelector(Selectors.NextPageLink) is not null;
        return new ParsedPage(products, hasNextPage);
    }

    private static string? TextOrNull(IElement tile, string selector)
    {
        var text = tile.QuerySelector(selector)?.TextContent?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static decimal? PriceOrNull(IElement tile, string selector)
    {
        var text = tile.QuerySelector(selector)?.TextContent;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digitsOnly = new string(text.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return decimal.TryParse(digitsOnly, out var value) ? value : null;
    }
}
