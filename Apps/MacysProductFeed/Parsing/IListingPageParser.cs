using MacysProductFeed.Scraping;

namespace MacysProductFeed.Parsing;

/// <summary>
/// Parses one already-fetched listing page into product records. The
/// orchestrator iterates its injected parser list, in order, and uses the
/// first non-null result (JSON-capture parser first, DOM fallback second,
/// per the Arch plan) — implementations decide for themselves which part of
/// <see cref="FetchedPage"/> they need and return null if they found no
/// usable data there, signaling the orchestrator to try the next parser.
/// Individual malformed tiles are skipped and reported via
/// <paramref name="onTileError"/> rather than failing the whole page.
/// </summary>
public interface IListingPageParser
{
    ParsedPage? TryParse(FetchedPage fetched, Action<string> onTileError);
}

public sealed record ParsedPage(IReadOnlyList<ProductRecord> Products, bool HasNextPage);
