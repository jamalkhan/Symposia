using MacysProductFeed.Cli;
using MacysProductFeed.Logging;
using MacysProductFeed.Output;
using MacysProductFeed.Parsing;
using MacysProductFeed.Pipeline;
using MacysProductFeed.Scraping;
using Microsoft.Extensions.Logging;

ScrapeOptions options;
try
{
    options = CliParser.Parse(args);
}
catch (CliValidationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var progressLogger = new ConsoleProgressLogger(loggerFactory.CreateLogger("MacysProductFeed"));

var rateLimiter = new RateLimiter(options.DelayMs);
await using var fetcher = new PlaywrightListingFetcher(rateLimiter);

ICsvFeedWriter csvWriter;
try
{
    csvWriter = new CsvFeedWriter(options.OutputPath);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Failed to open output path '{options.OutputPath}': {ex.Message}");
    return 1;
}

using (csvWriter)
{
    var deduper = new ProductDeduper();
    var summary = new RunSummary();
    IReadOnlyList<IListingPageParser> parsers = [new JsonListingParser(), new DomListingParser()];
    var retryBackoff = TimeSpan.FromMilliseconds(options.DelayMs * 2);

    var orchestrator = new ScrapeOrchestrator(fetcher, parsers, deduper, csvWriter, progressLogger, summary, retryBackoff);

    try
    {
        await orchestrator.RunAsync(options);
        csvWriter.Complete();
    }
    catch (BrowserLaunchException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unexpected error: {ex.Message}");
        return 1;
    }

    return summary.ExitCode;
}
