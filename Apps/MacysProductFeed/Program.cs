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
using var csvWriter = new CsvFeedWriter(options.OutputPath);

var deduper = new ProductDeduper();
var summary = new RunSummary();
IReadOnlyList<IListingPageParser> parsers = [new JsonListingParser(), new DomListingParser()];

var orchestrator = new ScrapeOrchestrator(fetcher, parsers, deduper, csvWriter, progressLogger, summary);
await orchestrator.RunAsync(options);

return summary.ExitCode;
