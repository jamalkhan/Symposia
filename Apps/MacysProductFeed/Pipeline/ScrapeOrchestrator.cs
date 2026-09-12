using MacysProductFeed.Cli;
using MacysProductFeed.Logging;
using MacysProductFeed.Output;
using MacysProductFeed.Parsing;
using MacysProductFeed.Scraping;

namespace MacysProductFeed.Pipeline;

/// <summary>
/// Drives the per-URL/per-page loop: fetch, parse (tries each injected
/// parser in order, JSON-capture then DOM fallback per the Arch plan),
/// dedup, write. Each page fetch+parse is wrapped in its own error boundary
/// so one bad page never aborts the run (issue #127 FR-8) — except a fatal
/// browser-launch failure, which aborts the whole run immediately per the
/// Arch plan ("no partial run is possible without a browser").
/// </summary>
public sealed class ScrapeOrchestrator(
    IListingFetcher fetcher,
    IReadOnlyList<IListingPageParser> parsers,
    ProductDeduper deduper,
    ICsvFeedWriter csvWriter,
    ConsoleProgressLogger logger,
    RunSummary summary,
    TimeSpan retryBackoff)
{
    /// <summary>One retry after a backoff on a transient page-fetch failure (issue #127 error-handling strategy).</summary>
    private const int MaxFetchAttempts = 2;

    /// <summary>
    /// Stop paginating a given seed URL after this many consecutive page
    /// failures, even though the URL's pagination isn't otherwise capped by
    /// --max-pages. Without this, a page whose content we can never
    /// successfully interpret (site markup changed, persistent block) would
    /// loop forever alongside the --page-parameter pagination heuristic,
    /// since we can't determine "no next page" from a page we couldn't parse.
    /// </summary>
    private const int MaxConsecutivePageFailures = 2;

    /// <summary>
    /// Hard ceiling on pages per seed URL when --max-pages is unset. This is
    /// not a spec requirement (default is "unlimited") — it's a safety net
    /// against the page-parameter pagination heuristic looping forever if a
    /// site never actually stops signaling "has next page" for a URL whose
    /// real pagination mechanism doesn't match our guess.
    /// </summary>
    private const int UnboundedSafetyPageCap = 1000;

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
        var pageCap = maxPages ?? UnboundedSafetyPageCap;
        var currentUrl = seedUrl;
        var pageNumber = 1;
        var consecutiveFailures = 0;

        while (pageNumber <= pageCap)
        {
            var (parsedPage, succeeded) = await FetchAndParsePageAsync(currentUrl, pageNumber, cancellationToken);
            summary.RecordPageProcessed();

            if (!succeeded || parsedPage is null)
            {
                summary.RecordPageError();
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutivePageFailures)
                {
                    break;
                }

                // We don't know whether there's a next page from a page we
                // couldn't interpret; keep advancing the pagination guess
                // rather than abandoning the rest of this URL's results
                // outright (issue #127 FR-8: "continues processing the
                // remaining pages").
                pageNumber++;
                currentUrl = BuildPageUrl(seedUrl, pageNumber);
                continue;
            }

            consecutiveFailures = 0;

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
        FetchedPage fetched;
        var attempt = 1;
        while (true)
        {
            try
            {
                fetched = await fetcher.FetchAsync(url, cancellationToken);
                break;
            }
            catch (BrowserLaunchException)
            {
                // Fatal — no per-page retry/skip makes sense without a browser at all.
                throw;
            }
            catch (Exception ex) when (attempt < MaxFetchAttempts)
            {
                logger.PageError(url, pageNumber, $"Fetch attempt {attempt} failed, retrying after backoff: {ex.Message}");
                await Task.Delay(retryBackoff, cancellationToken);
                attempt++;
            }
            catch (Exception ex)
            {
                logger.PageError(url, pageNumber, $"Fetch failed: {ex.Message}");
                return (null, false);
            }
        }

        // Parsing failures are not retried: a bad tile/page shape won't be
        // fixed by re-fetching, and retrying would burn an extra request
        // against the live site for a purely local bug (Arch plan's error
        // strategy: "no retry on parse-shape errors").
        ParsedPage? page;
        try
        {
            page = ParsePage(fetched);
        }
        catch (Exception ex)
        {
            logger.PageError(url, pageNumber, $"Parse failed: {ex.Message}");
            return (null, false);
        }

        if (page is null)
        {
            // No parser in the chain recognized this page's content at all —
            // distinct from a parser recognizing the page and legitimately
            // finding zero products. Treat as a page-level failure so it's
            // never silently counted as a successful "0 products" page
            // (QA test case 13's "not a crash, but also not silent success").
            logger.PageError(url, pageNumber, "No parser recognized this page's content.");
            return (null, false);
        }

        return (page, true);
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

    private ParsedPage? ParsePage(FetchedPage fetched)
    {
        void OnTileError(string message)
        {
            summary.RecordTileError();
            logger.TileError(message);
        }

        foreach (var parser in parsers)
        {
            var result = parser.TryParse(fetched, OnTileError);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
