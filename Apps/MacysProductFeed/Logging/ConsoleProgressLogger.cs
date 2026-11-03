using Microsoft.Extensions.Logging;

namespace MacysProductFeed.Logging;

/// <summary>
/// Thin wrapper around Microsoft.Extensions.Logging giving the orchestrator
/// a small, purpose-named surface for FR-9 progress lines and FR-8 warnings,
/// rather than sprinkling raw ILogger calls with ad hoc message templates
/// throughout the pipeline.
/// </summary>
public sealed class ConsoleProgressLogger(ILogger logger)
{
    public void ListingStarted(string url) =>
        logger.LogInformation("Starting listing: {Url}", url);

    public void PageProcessed(string url, int pageNumber, int productsFound) =>
        logger.LogInformation("Page {PageNumber} of {Url}: {ProductsFound} product(s) found", pageNumber, url, productsFound);

    public void PageError(string url, int pageNumber, string message) =>
        logger.LogWarning("Page {PageNumber} of {Url} failed: {Message}", pageNumber, url, message);

    public void TileError(string message) =>
        logger.LogWarning("{Message}", message);

    public void RunComplete(string summary) =>
        logger.LogInformation("{Summary}", summary);
}
