using MacysProductFeed.Parsing;

namespace MacysProductFeed.Pipeline;

/// <summary>
/// Dedupes by product ID across all URLs/pages in a run. First occurrence
/// wins (issue #127 post-huddle clarification) — later duplicates are
/// counted but discarded.
/// </summary>
public sealed class ProductDeduper
{
    private readonly Dictionary<string, ProductRecord> _seen = new();
    public int DuplicateCount { get; private set; }

    /// <summary>Returns true if this is a new product (added), false if it was a duplicate.</summary>
    public bool TryAdd(ProductRecord record)
    {
        if (_seen.ContainsKey(record.ProductId))
        {
            DuplicateCount++;
            return false;
        }

        _seen.Add(record.ProductId, record);
        return true;
    }

    public IReadOnlyCollection<ProductRecord> Records => _seen.Values;
}
