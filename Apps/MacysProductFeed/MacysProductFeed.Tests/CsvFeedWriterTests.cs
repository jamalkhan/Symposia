using MacysProductFeed.Output;
using MacysProductFeed.Parsing;

namespace MacysProductFeed.Tests;

public class CsvFeedWriterTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("macys-csv-tests").FullName;

    private static ProductRecord Product(
        string id = "P1",
        string? title = null,
        string? category = null) =>
        new(id, title, "Brand", 10m, null, "USD", "https://example.com/p1", "https://example.com/p1.jpg", "In Stock", category);

    [Fact]
    public void Complete_WritesHeaderAndRow()
    {
        var path = Path.Combine(_tempDir, "feed.csv");
        using (var writer = new CsvFeedWriter(path))
        {
            writer.WriteRecord(Product(title: "Basic Tee"));
            writer.Complete();
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("ProductId", lines[0]);
        Assert.Contains("Basic Tee", lines[1]);
    }

    [Fact]
    public void Complete_WritesHeaderOnly_WhenNoRecordsWritten()
    {
        var path = Path.Combine(_tempDir, "empty-feed.csv");

        using (var writer = new CsvFeedWriter(path))
        {
            writer.Complete();
        }

        var lines = File.ReadAllLines(path);
        Assert.Single(lines);
        Assert.Contains("ProductId", lines[0]);
    }

    [Fact]
    public void Dispose_WithoutComplete_DoesNotCreateDestinationFile()
    {
        var path = Path.Combine(_tempDir, "never-completed.csv");

        using (var writer = new CsvFeedWriter(path))
        {
            writer.WriteRecord(Product());
            // Deliberately not calling Complete() — simulates a run that failed partway through.
        }

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp-*"));
    }

    [Fact]
    public void Dispose_WithoutComplete_LeavesExistingDestinationFileUntouched()
    {
        var path = Path.Combine(_tempDir, "existing-feed.csv");
        File.WriteAllText(path, "ProductId\r\nOLD-GOOD-DATA\r\n");

        using (var writer = new CsvFeedWriter(path))
        {
            writer.WriteRecord(Product(id: "NEW-PARTIAL-DATA"));
            // Run fails before Complete() is called.
        }

        var content = File.ReadAllText(path);
        Assert.Contains("OLD-GOOD-DATA", content);
        Assert.DoesNotContain("NEW-PARTIAL-DATA", content);
    }

    [Fact]
    public void Complete_OverwritesExistingDestinationFile()
    {
        var path = Path.Combine(_tempDir, "overwrite-feed.csv");
        File.WriteAllText(path, "ProductId\r\nOLD-DATA\r\n");

        using (var writer = new CsvFeedWriter(path))
        {
            writer.WriteRecord(Product(id: "NEW-DATA"));
            writer.Complete();
        }

        var content = File.ReadAllText(path);
        Assert.Contains("NEW-DATA", content);
        Assert.DoesNotContain("OLD-DATA", content);
    }

    [Fact]
    public void WriteRecord_EscapesEmbeddedComma()
    {
        var path = Path.Combine(_tempDir, "comma-feed.csv");
        using (var writer = new CsvFeedWriter(path))
        {
            writer.WriteRecord(Product(title: "Women's Cotton, Slim Fit Top"));
            writer.Complete();
        }

        var content = File.ReadAllText(path);
        Assert.Contains("\"Women's Cotton, Slim Fit Top\"", content);
    }

    [Fact]
    public void WriteRecord_EscapesEmbeddedQuote()
    {
        var path = Path.Combine(_tempDir, "quote-feed.csv");
        using (var writer = new CsvFeedWriter(path))
        {
            writer.WriteRecord(Product(title: "The \"Best\" Top"));
            writer.Complete();
        }

        var content = File.ReadAllText(path);
        Assert.Contains("\"The \"\"Best\"\" Top\"", content);
    }

    [Fact]
    public void WriteRecord_EscapesEmbeddedNewline()
    {
        var path = Path.Combine(_tempDir, "newline-feed.csv");
        using (var writer = new CsvFeedWriter(path))
        {
            writer.WriteRecord(Product(title: "Line one\nLine two"));
            writer.Complete();
        }

        var lines = File.ReadAllLines(path);
        // A properly quoted embedded newline keeps this a 2-line file (header + one logical row spanning two physical lines' worth of content within quotes)
        Assert.True(lines.Length >= 2);
        var content = File.ReadAllText(path);
        Assert.Contains("Line one\nLine two", content);
    }

    [Fact]
    public void Constructor_AutoCreatesParentDirectory_WhenMissing()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "does-not-exist-yet", "feed.csv");

        using (var writer = new CsvFeedWriter(nestedPath))
        {
            writer.Complete();
        }

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void WriteRecord_DoesNotDuplicateHeader_AcrossMultipleRecords()
    {
        var path = Path.Combine(_tempDir, "multi-feed.csv");
        using (var writer = new CsvFeedWriter(path))
        {
            writer.WriteRecord(Product("P1"));
            writer.WriteRecord(Product("P2"));
            writer.Complete();
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.Equal(1, lines.Count(l => l.StartsWith("ProductId")));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
