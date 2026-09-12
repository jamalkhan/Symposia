namespace MacysProductFeed.Parsing;

/// <summary>
/// Parses one already-fetched listing page's captured source data into
/// product records. Implementations are tried in order by the orchestrator
/// (JSON-capture preferred, DOM fallback) — both map to the same
/// <see cref="ProductRecord"/> shape so downstream code is agnostic to which
/// path produced a given record.
/// </summary>
public interface IListingPageParser
{
    /// <summary>
    /// Attempts to parse <paramref name="source"/> into product records.
    /// Returns null if this parser found no usable data (e.g. no JSON
    /// payload was captured), signaling the orchestrator to try the next
    /// parser in the chain. Individual malformed tiles are skipped and
    /// reported via <paramref name="onTileError"/> rather than failing the
    /// whole page.
    /// </summary>
    ParsedPage? TryParse(string source, Action<string> onTileError);
}

public sealed record ParsedPage(IReadOnlyList<ProductRecord> Products, bool HasNextPage);
