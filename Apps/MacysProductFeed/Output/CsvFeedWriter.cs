using System.Globalization;
using CsvHelper;
using MacysProductFeed.Parsing;

namespace MacysProductFeed.Output;

/// <summary>
/// RFC 4180-compliant CSV writer (issue #127 FR-5), backed by CsvHelper
/// rather than hand-rolled string joins. Streams rows as they're written
/// rather than buffering the whole feed in memory, so a long run's progress
/// survives an unexpected interruption. Auto-creates the output path's
/// parent directory if it doesn't exist (post-huddle clarification).
/// </summary>
public sealed class CsvFeedWriter : ICsvFeedWriter
{
    private readonly StreamWriter _streamWriter;
    private readonly CsvWriter _csvWriter;
    private bool _headerWritten;

    public CsvFeedWriter(string outputPath)
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        _streamWriter = new StreamWriter(outputPath, append: false);
        _csvWriter = new CsvWriter(_streamWriter, CultureInfo.InvariantCulture);
    }

    public void WriteRecord(ProductRecord record)
    {
        if (!_headerWritten)
        {
            _csvWriter.WriteHeader<ProductRecord>();
            _csvWriter.NextRecord();
            _headerWritten = true;
        }

        _csvWriter.WriteRecord(record);
        _csvWriter.NextRecord();
    }

    public void Dispose()
    {
        if (!_headerWritten)
        {
            // No products were written; still emit a header-only file (post-huddle: complete-failure case).
            _csvWriter.WriteHeader<ProductRecord>();
            _csvWriter.NextRecord();
        }

        _csvWriter.Dispose();
        _streamWriter.Dispose();
    }
}
