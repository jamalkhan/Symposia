using MacysProductFeed.Cli;

namespace MacysProductFeed.Tests;

public class CliParserTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("macys-cli-tests").FullName;

    [Fact]
    public void Parse_Throws_WhenNoUrlSourceProvided()
    {
        Assert.Throws<CliValidationException>(() => CliParser.Parse([]));
    }

    [Fact]
    public void Parse_SingleUrl_ProducesExpectedOptions()
    {
        var options = CliParser.Parse(["--url", "https://www.macys.com/shop/womens-clothing"]);

        Assert.Single(options.Urls);
        Assert.Equal(ScrapeOptions.DefaultOutputPath, options.OutputPath);
        Assert.Equal(ScrapeOptions.DefaultDelayMs, options.DelayMs);
        Assert.Null(options.MaxPages);
    }

    [Fact]
    public void Parse_MultipleUrlArgs_AreAllIncluded()
    {
        var options = CliParser.Parse([
            "--url", "https://www.macys.com/a",
            "--url", "https://www.macys.com/b"
        ]);

        Assert.Equal(2, options.Urls.Count);
    }

    [Fact]
    public void Parse_UrlFile_BlankLinesIgnored()
    {
        var path = Path.Combine(_tempDir, "urls.txt");
        File.WriteAllText(path, "https://www.macys.com/a\n\nhttps://www.macys.com/b\n\n");

        var options = CliParser.Parse(["--url-file", path]);

        Assert.Equal(2, options.Urls.Count);
    }

    [Fact]
    public void Parse_UrlAndUrlFileCombined_ProducesUnion()
    {
        var path = Path.Combine(_tempDir, "urls.txt");
        File.WriteAllText(path, "https://www.macys.com/from-file\n");

        var options = CliParser.Parse([
            "--url", "https://www.macys.com/from-arg",
            "--url-file", path
        ]);

        Assert.Equal(2, options.Urls.Count);
        Assert.Contains("https://www.macys.com/from-arg", options.Urls);
        Assert.Contains("https://www.macys.com/from-file", options.Urls);
    }

    [Fact]
    public void Parse_Throws_WhenUrlFileDoesNotExist()
    {
        Assert.Throws<CliValidationException>(() =>
            CliParser.Parse(["--url-file", Path.Combine(_tempDir, "missing.txt")]));
    }

    [Fact]
    public void Parse_Throws_WhenUrlFileIsEmpty()
    {
        var path = Path.Combine(_tempDir, "empty.txt");
        File.WriteAllText(path, "");

        Assert.Throws<CliValidationException>(() => CliParser.Parse(["--url-file", path]));
    }

    [Fact]
    public void Parse_MaxPages_IsRespected()
    {
        var options = CliParser.Parse(["--url", "https://www.macys.com/a", "--max-pages", "3"]);

        Assert.Equal(3, options.MaxPages);
    }

    [Fact]
    public void Parse_DelayMsZero_IsAllowed()
    {
        var options = CliParser.Parse(["--url", "https://www.macys.com/a", "--delay-ms", "0"]);

        Assert.Equal(0, options.DelayMs);
    }

    [Fact]
    public void Parse_Throws_WhenDelayMsNegative()
    {
        Assert.Throws<CliValidationException>(() =>
            CliParser.Parse(["--url", "https://www.macys.com/a", "--delay-ms", "-1"]));
    }

    [Fact]
    public void Parse_CustomOutputPath_IsRespected()
    {
        var options = CliParser.Parse(["--url", "https://www.macys.com/a", "--output", "custom.csv"]);

        Assert.Equal("custom.csv", options.OutputPath);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
