using MacysProductFeed.Parsing;

namespace MacysProductFeed.Output;

public interface ICsvFeedWriter : IDisposable
{
    void WriteRecord(ProductRecord record);
}
