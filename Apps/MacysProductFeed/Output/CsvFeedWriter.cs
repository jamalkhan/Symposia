using System.Globalization;
using CsvHelper;
using MacysProductFeed.Parsing;

namespace MacysProductFeed.Output;

/// <summary>
/// RFC 4180-compliant CSV writer (issue #127 FR-5), backed by CsvHelper
/// rather than hand-rolled string joins. Writes to a temporary file
/// alongside the destination and only renames it into place on an explicit
/// <see cref="Complete"/> call, so a run that fails partway through never
/// clobbers a previously-good feed at the same output path (a truncate-in-
/// place write would otherwise destroy it the instant the file is opened).
/// Auto-flushes so buffered rows aren't lost to a hard process crash before
/// the eventual <see cref="Dispose"/>.
/// </summary>
public sealed class CsvFeedWriter : ICsvFeedWriter
{
    private readonly string _destinationPath;
    private readonly string _tempPath;
    private readonly StreamWriter _streamWriter;
    private readonly CsvWriter _csvWriter;
    private bool _headerWritten;
    private bool _completed;
    private bool _closed;

    public CsvFeedWriter(string outputPath)
    {
        _destinationPath = Path.GetFullPath(outputPath);
        var parentDirectory = Path.GetDirectoryName(_destinationPath);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        _tempPath = _destinationPath + $".tmp-{Guid.NewGuid():N}";
        _streamWriter = new StreamWriter(_tempPath, append: false) { AutoFlush = true };
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

    public void Complete()
    {
        if (!_headerWritten)
        {
            // No products were written; still emit a header-only file (post-huddle: complete-failure case).
            _csvWriter.WriteHeader<ProductRecord>();
            _csvWriter.NextRecord();
            _headerWritten = true;
        }

        CloseWriters();

        File.Move(_tempPath, _destinationPath, overwrite: true);
        _completed = true;
    }

    public void Dispose()
    {
        CloseWriters();

        if (!_completed && File.Exists(_tempPath))
        {
            // Run never completed successfully — leave the destination path
            // (any prior good feed) untouched and clean up the partial temp file.
            File.Delete(_tempPath);
        }
    }

    private void CloseWriters()
    {
        if (_closed)
        {
            return;
        }

        _csvWriter.Dispose();
        _streamWriter.Dispose();
        _closed = true;
    }
}
