namespace MacysProductFeed.Scraping;

public sealed record FetchedPage(string? CapturedJson, string RenderedHtml);

/// <summary>
/// Fetches (renders) one listing page and returns both any JSON payload
/// captured from the page's own network traffic and the rendered HTML, so
/// the orchestrator can hand both to the parser chain (JSON preferred, DOM
/// fallback).
/// </summary>
public interface IListingFetcher : IAsyncDisposable
{
    Task<FetchedPage> FetchAsync(string url, CancellationToken cancellationToken = default);
}
