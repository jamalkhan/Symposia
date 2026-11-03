using MacysProductFeed.Parsing;

namespace MacysProductFeed.Output;

public interface ICsvFeedWriter : IDisposable
{
    void WriteRecord(ProductRecord record);

    /// <summary>
    /// Commits the feed to its final destination path. Must be called
    /// explicitly after a successful run — if the run fails and this is
    /// never called, the previous good feed at the destination path (if any)
    /// is left untouched rather than being clobbered by a partial write.
    /// </summary>
    void Complete();
}
