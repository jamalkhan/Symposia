using MacysProductFeed.Cli;
using MacysProductFeed.Logging;
using MacysProductFeed.Output;
using MacysProductFeed.Parsing;
using MacysProductFeed.Scraping;

namespace MacysProductFeed.Pipeline;

/// <summary>
/// Drives the per-URL/per-page loop: fetch, parse (JSON preferred, DOM
/// fallback), dedup, write. Each page fetch+parse is wrapped in its own
/// error boundary so one bad page never aborts the run (issue #127 FR-8).
/// </summary>
public sealed class ScrapeOrchestrator(
    IListingFetcher fetcher,
    IReadOnlyList<IListingPageParser> parsers,
    ProductDeduper deduper,
    ICsvFeedWriter csvWriter,
    ConsoleProgressLogger logger,
    RunSummary summary)
{
    /// <summary>One retry after a short backoff on a transient page-level failure (issue #127 error-handling strategy).</summary>
    private const int MaxAttemptsPerPage = 2;

    public async Task RunAsync(ScrapeOptions options, CancellationToken cancellationToken = default)
    {
        foreach (var seedUrl in options.Urls)
        {
            logger.ListingStarted(seedUrl);
            await ProcessListingAsync(seedUrl, options.MaxPages, cancellationToken);
        }

        logger.RunComplete(summary.ToString());
    }

    private async Task ProcessListingAsync(string seedUrl, int? maxPages, CancellationToken cancellationToken)
    {
        string? currentUrl = seedUrl;
        var pageNumber = 1;

        while (currentUrl is not null && (maxPages is null || pageNumber <= maxPages))
        {
            var (parsedPage, succeeded) = await FetchAndParsePageAsync(currentUrl, pageNumber, cancellationToken);
            summary.RecordPageProcessed();

            if (!succeeded || parsedPage is null)
            {
                summary.RecordPageError();
                break;
            }

            int newProducts = 0;
            foreach (var product in parsedPage.Products)
            {
                if (deduper.TryAdd(product))
                {
                    csvWriter.WriteRecord(product);
                    newProducts++;
                }
                else
                {
                    summary.RecordDuplicatesSkipped(1);
                }
            }

            summary.RecordProductsWritten(newProducts);
            logger.PageProcessed(seedUrl, pageNumber, parsedPage.Products.Count);

            if (!parsedPage.HasNextPage)
            {
                break;
            }

            pageNumber++;
            currentUrl = BuildPageUrl(seedUrl, pageNumber);
        }
    }

    private async Task<(ParsedPage? Page, bool Succeeded)> FetchAndParsePageAsync(
        string url, int pageNumber, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttemptsPerPage; attempt++)
        {
            try
            {
                var fetched = await fetcher.FetchAsync(url, cancellationToken);
                var page = ParsePage(fetched);
                return (page, true);
            }
            catch (Exception ex) when (attempt < MaxAttemptsPerPage)
            {
                logger.PageError(url, pageNumber, $"Attempt {attempt} failed, retrying: {ex.Message}");
            }
            catch (Exception ex)
            {
                logger.PageError(url, pageNumber, ex.Message);
                return (null, false);
            }
        }

        return (null, false);
    }

    /// <summary>
    /// Builds the URL for a given page number by setting a "page" query
    /// parameter. This is a best-effort heuristic pending confirmation of
    /// macys.com's actual pagination mechanism (flagged in the Arch plan as
    /// an implementation-time spike, not an architectural blocker) — the
    /// fetcher/parser abstraction accommodates whichever mechanism a future
    /// change discovers instead.
    /// </summary>
    private static string BuildPageUrl(string seedUrl, int pageNumber)
    {
        var builder = new UriBuilder(seedUrl);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query["page"] = pageNumber.ToString();
        builder.Query = query.ToString();
        return builder.Uri.ToString();
    }

    private ParsedPage ParsePage(FetchedPage fetched)
    {
        void OnTileError(string message)
        {
            summary.RecordTileError();
            logger.TileError(message);
        }

        if (fetched.CapturedJson is not null)
        {
            var jsonResult = parsers.OfType<JsonListingParser>().FirstOrDefault()?.TryParse(fetched.CapturedJson, OnTileError);
            if (jsonResult is not null)
            {
                return jsonResult;
            }
        }

        var domParser = parsers.OfType<DomListingParser>().FirstOrDefault();
        return domParser?.TryParse(fetched.RenderedHtml, OnTileError)
            ?? new ParsedPage(Array.Empty<ProductRecord>(), HasNextPage: false);
    }
}
