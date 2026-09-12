namespace MacysProductFeed.Parsing;

/// <summary>
/// A single scraped product, matching the CSV feed schema (issue #127 FR-4/FR-5).
/// ProductId is the only required field — a tile without one cannot be deduped
/// or keyed and is treated as a tile-level parse failure instead of a record.
/// </summary>
public sealed record ProductRecord(
    string ProductId,
    string? Title,
    string? Brand,
    decimal? RegularPrice,
    decimal? SalePrice,
    string Currency,
    string? ProductUrl,
    string? ImageUrl,
    string? Availability,
    string? Category);
