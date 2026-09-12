using System.Text.Json;

namespace MacysProductFeed.Parsing;

/// <summary>
/// Preferred parser: reads product data out of a JSON payload captured from
/// the listing page's own network traffic (see PlaywrightListingFetcher),
/// rather than screen-scraping HTML. More resilient to markup churn than the
/// DOM fallback since it reads the same structured data the page itself
/// consumes to render the grid.
/// </summary>
public sealed class JsonListingParser : IListingPageParser
{
    public ParsedPage? TryParse(string source, Action<string> onTileError)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!TryFindProductArray(document.RootElement, out var productArray))
            {
                return null;
            }

            var products = new List<ProductRecord>(productArray.GetArrayLength());
            foreach (var item in productArray.EnumerateArray())
            {
                try
                {
                    var productId = GetString(item, "productId", "id", "styleNumber");
                    if (string.IsNullOrWhiteSpace(productId))
                    {
                        onTileError("Product entry missing product ID; skipped.");
                        continue;
                    }

                    products.Add(new ProductRecord(
                        ProductId: productId,
                        Title: GetString(item, "title", "name", "productName"),
                        Brand: GetString(item, "brand", "brandName"),
                        RegularPrice: GetDecimal(item, "regularPrice", "price"),
                        SalePrice: GetDecimal(item, "salePrice", "offerPrice"),
                        Currency: "USD",
                        ProductUrl: GetString(item, "productUrl", "url", "link"),
                        ImageUrl: GetString(item, "imageUrl", "image", "imageLink"),
                        Availability: GetString(item, "availability", "stockStatus"),
                        Category: GetString(item, "category", "categoryName")));
                }
                catch (Exception ex)
                {
                    onTileError($"Failed to parse product entry: {ex.Message}");
                }
            }

            var hasNextPage = TryGetBool(document.RootElement, "hasNextPage")
                ?? TryGetBool(document.RootElement, "hasMore")
                ?? false;

            return new ParsedPage(products, hasNextPage);
        }
    }

    private static bool TryFindProductArray(JsonElement root, out JsonElement array)
    {
        foreach (var candidateName in new[] { "products", "items", "results" })
        {
            if (root.TryGetProperty(candidateName, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
            {
                array = candidate;
                return true;
            }
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }

        array = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }

        return null;
    }

    private static decimal? GetDecimal(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return null;
    }
}
