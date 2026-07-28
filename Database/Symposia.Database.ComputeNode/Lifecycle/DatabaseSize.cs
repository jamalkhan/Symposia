namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>Compute sizes per database-billing.md's size table, `micro` through `4xlarge`.</summary>
public enum DatabaseSize
{
    Micro,
    Small,
    Medium,
    Large,
    XLarge,
    TwoXLarge,
    FourXLarge,
}

/// <summary>
/// Maps each compute size to the minimum compute tier it requires (FR7: a resize to a size
/// requiring a higher tier than the current node supports must trigger reassignment).
/// </summary>
public static class DatabaseSizeExtensions
{
    public static int RequiredTier(this DatabaseSize size) => size switch
    {
        DatabaseSize.Micro or DatabaseSize.Small or DatabaseSize.Medium => 3,
        DatabaseSize.Large or DatabaseSize.XLarge => 2,
        DatabaseSize.TwoXLarge or DatabaseSize.FourXLarge => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };
}
