using Microsoft.Playwright;

namespace MacysProductFeed.Scraping;

/// <summary>
/// Owns the Playwright browser/page lifecycle: renders a listing page (so
/// client-side-hydrated content is visible, unlike a plain HTTP GET),
/// captures any JSON the page fetches to populate its own grid, and applies
/// the rate limiter before every navigation. One browser instance is reused
/// across all pages in a run.
/// </summary>
public sealed class PlaywrightListingFetcher : IListingFetcher
{
    /// <summary>Default page load/render timeout (issue #127 post-huddle clarification).</summary>
    public const int DefaultTimeoutMs = 30_000;

    private readonly RateLimiter _rateLimiter;
    private readonly int _timeoutMs;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PlaywrightListingFetcher(RateLimiter rateLimiter, int timeoutMs = DefaultTimeoutMs)
    {
        _rateLimiter = rateLimiter;
        _timeoutMs = timeoutMs;
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is not null)
        {
            return _browser;
        }

        _playwright = await Playwright.CreateAsync();
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                "Failed to launch headless Chromium. Run 'playwright install chromium' before using this tool.", ex);
        }

        return _browser;
    }

    public async Task<FetchedPage> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        await _rateLimiter.WaitAsync(cancellationToken);

        var browser = await GetBrowserAsync();
        var page = await browser.NewPageAsync();
        try
        {
            string? capturedJson = null;
            page.Response += async (_, response) =>
            {
                if (capturedJson is not null)
                {
                    return;
                }

                if (response.Request.ResourceType is "xhr" or "fetch" &&
                    response.Headers.TryGetValue("content-type", out var contentType) &&
                    contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        capturedJson = await response.TextAsync();
                    }
                    catch (PlaywrightException)
                    {
                        // Response body no longer available (e.g. navigation moved on); ignore.
                    }
                }
            };

            await page.GotoAsync(url, new PageGotoOptions { Timeout = _timeoutMs, WaitUntil = WaitUntilState.NetworkIdle });
            var html = await page.ContentAsync();
            return new FetchedPage(capturedJson, html);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}
