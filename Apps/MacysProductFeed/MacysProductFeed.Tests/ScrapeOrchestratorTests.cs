using MacysProductFeed.Cli;
using MacysProductFeed.Logging;
using MacysProductFeed.Output;
using MacysProductFeed.Parsing;
using MacysProductFeed.Pipeline;
using MacysProductFeed.Scraping;
using Microsoft.Extensions.Logging.Abstractions;

namespace MacysProductFeed.Tests;

public class ScrapeOrchestratorTests
{
    private const string SeedUrl = "https://www.macys.com/shop/some-category";

    private static ProductRecord Product(string id) =>
        new(id, null, null, null, null, "USD", null, null, null, null);

    private static ConsoleProgressLogger Logger() => new(NullLogger.Instance);

    /// <summary>Fetcher whose per-URL behavior is fully scripted by the test.</summary>
    private sealed class FakeListingFetcher(Func<string, int, FetchedPage> onFetch) : IListingFetcher
    {
        public List<string> FetchedUrls { get; } = [];

        public Task<FetchedPage> FetchAsync(string url, CancellationToken cancellationToken = default)
        {
            FetchedUrls.Add(url);
            var callCountForUrl = FetchedUrls.Count(u => u == url);
            return Task.FromResult(onFetch(url, callCountForUrl));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Parser whose per-page result is fully scripted by the test — bypasses JSON/DOM entirely to isolate orchestration logic.</summary>
    private sealed class FakeListingPageParser(Func<FetchedPage, ParsedPage?> onParse) : IListingPageParser
    {
        public ParsedPage? TryParse(FetchedPage fetched, Action<string> onTileError) => onParse(fetched);
    }

    private sealed class FakeCsvFeedWriter : ICsvFeedWriter
    {
        public List<ProductRecord> WrittenRecords { get; } = [];
        public bool Completed { get; private set; }

        public void WriteRecord(ProductRecord record) => WrittenRecords.Add(record);
        public void Complete() => Completed = true;
        public void Dispose() { }
    }

    private static FetchedPage PageContent(string marker) => new(null, marker);

    private static int PageNumberFromUrl(string url) =>
        url.Contains("page=") ? int.Parse(new Uri(url).Query.Split("page=")[1].Split('&')[0]) : 1;

    [Fact]
    public async Task RunAsync_AccumulatesProductsAcrossMultiplePages()
    {
        var fetcher = new FakeListingFetcher((url, _) => PageContent(url));
        var parser = new FakeListingPageParser(fetched =>
        {
            var page = PageNumberFromUrl(fetched.RenderedHtml);
            return new ParsedPage([Product($"P{page}")], HasNextPage: page < 3);
        });
        var writer = new FakeCsvFeedWriter();
        var summary = new RunSummary();
        var orchestrator = new ScrapeOrchestrator(
            fetcher, [parser], new ProductDeduper(), writer, Logger(), summary, TimeSpan.Zero);

        await orchestrator.RunAsync(new ScrapeOptions([SeedUrl], "out.csv", MaxPages: null, DelayMs: 0));

        Assert.Equal(3, writer.WrittenRecords.Count);
        Assert.Equal(3, summary.ProductsWritten);
        Assert.Equal(3, summary.PagesProcessed);
        Assert.Equal(0, summary.PageErrors);
    }

    [Fact]
    public async Task RunAsync_RespectsMaxPages()
    {
        var fetcher = new FakeListingFetcher((url, _) => PageContent(url));
        var parser = new FakeListingPageParser(_ => new ParsedPage([Product(Guid.NewGuid().ToString())], HasNextPage: true));
        var writer = new FakeCsvFeedWriter();
        var summary = new RunSummary();
        var orchestrator = new ScrapeOrchestrator(
            fetcher, [parser], new ProductDeduper(), writer, Logger(), summary, TimeSpan.Zero);

        await orchestrator.RunAsync(new ScrapeOptions([SeedUrl], "out.csv", MaxPages: 2, DelayMs: 0));

        Assert.Equal(2, summary.PagesProcessed);
        Assert.Equal(2, writer.WrittenRecords.Count);
    }

    [Fact]
    public async Task RunAsync_ContinuesPastASinglePageFailure_RatherThanAbandoningRemainingPages()
    {
        var fetcher = new FakeListingFetcher((url, callCount) =>
        {
            var page = PageNumberFromUrl(url);
            if (page == 2)
            {
                throw new InvalidOperationException("simulated transient failure");
            }

            return PageContent(url);
        });
        var parser = new FakeListingPageParser(fetched =>
        {
            var page = PageNumberFromUrl(fetched.RenderedHtml);
            return new ParsedPage([Product($"P{page}")], HasNextPage: page < 3);
        });
        var writer = new FakeCsvFeedWriter();
        var summary = new RunSummary();
        var orchestrator = new ScrapeOrchestrator(
            fetcher, [parser], new ProductDeduper(), writer, Logger(), summary, TimeSpan.Zero);

        await orchestrator.RunAsync(new ScrapeOptions([SeedUrl], "out.csv", MaxPages: null, DelayMs: 0));

        // Page 2 fails outright (both fetch attempts throw), but page 3 is still attempted and succeeds.
        Assert.Equal(1, summary.PageErrors);
        Assert.Equal(2, writer.WrittenRecords.Count); // page 1 and page 3 products
        Assert.Contains(writer.WrittenRecords, p => p.ProductId == "P1");
        Assert.Contains(writer.WrittenRecords, p => p.ProductId == "P3");
    }

    [Fact]
    public async Task RunAsync_StopsAfterConsecutivePageFailureThreshold_InsteadOfLoopingForever()
    {
        var fetcher = new FakeListingFetcher((_, _) => throw new InvalidOperationException("always fails"));
        var parser = new FakeListingPageParser(_ => null);
        var writer = new FakeCsvFeedWriter();
        var summary = new RunSummary();
        var orchestrator = new ScrapeOrchestrator(
            fetcher, [parser], new ProductDeduper(), writer, Logger(), summary, TimeSpan.Zero);

        // Regression guard for an unbounded-loop bug: if this hangs, pagination
        // never terminates on a page that can never be fetched successfully.
        var completed = await Task.WhenAny(
            orchestrator.RunAsync(new ScrapeOptions([SeedUrl], "out.csv", MaxPages: null, DelayMs: 0)),
            Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(completed is Task t && t.IsCompletedSuccessfully, "Orchestrator did not terminate a persistently-failing URL's pagination.");
        Assert.Equal(2, summary.PagesProcessed); // MaxConsecutivePageFailures
        Assert.Equal(2, summary.PageErrors);
        Assert.Empty(writer.WrittenRecords);
    }

    [Fact]
    public async Task RunAsync_TreatsUnrecognizedPageContentAsAnError_NotASilentZeroProductSuccess()
    {
        var fetcher = new FakeListingFetcher((url, _) => PageContent(url));
        var parser = new FakeListingPageParser(_ => null); // no parser recognizes this content at all
        var writer = new FakeCsvFeedWriter();
        var summary = new RunSummary();
        var orchestrator = new ScrapeOrchestrator(
            fetcher, [parser], new ProductDeduper(), writer, Logger(), summary, TimeSpan.Zero);

        await orchestrator.RunAsync(new ScrapeOptions([SeedUrl], "out.csv", MaxPages: 1, DelayMs: 0));

        Assert.Equal(1, summary.PageErrors);
        Assert.Equal(0, summary.ProductsWritten);
    }

    [Fact]
    public async Task RunAsync_DedupsProductsAcrossPages()
    {
        var fetcher = new FakeListingFetcher((url, _) => PageContent(url));
        var callIndex = 0;
        var parser = new FakeListingPageParser(_ =>
        {
            callIndex++;
            return new ParsedPage([Product("P1")], HasNextPage: callIndex < 2);
        });
        var writer = new FakeCsvFeedWriter();
        var summary = new RunSummary();
        var orchestrator = new ScrapeOrchestrator(
            fetcher, [parser], new ProductDeduper(), writer, Logger(), summary, TimeSpan.Zero);

        await orchestrator.RunAsync(new ScrapeOptions([SeedUrl], "out.csv", MaxPages: null, DelayMs: 0));

        Assert.Single(writer.WrittenRecords);
        Assert.Equal(1, summary.ProductsWritten);
        Assert.Equal(1, summary.DuplicatesSkipped);
    }

    [Fact]
    public async Task RunAsync_RecoversAfterATransientFetchFailure_WithoutCountingItAsAPageError()
    {
        var attemptsForSeedUrl = 0;
        var fetcher = new FakeListingFetcher((url, callCount) =>
        {
            if (url == SeedUrl)
            {
                attemptsForSeedUrl++;
                if (attemptsForSeedUrl == 1)
                {
                    throw new InvalidOperationException("transient");
                }
            }

            return PageContent(url);
        });
        var parser = new FakeListingPageParser(_ => new ParsedPage([Product("P1")], HasNextPage: false));
        var writer = new FakeCsvFeedWriter();
        var summary = new RunSummary();
        var orchestrator = new ScrapeOrchestrator(
            fetcher, [parser], new ProductDeduper(), writer, Logger(), summary, TimeSpan.Zero);

        await orchestrator.RunAsync(new ScrapeOptions([SeedUrl], "out.csv", MaxPages: null, DelayMs: 0));

        Assert.Equal(2, attemptsForSeedUrl); // failed once, succeeded on retry
        Assert.Equal(0, summary.PageErrors);
        Assert.Single(writer.WrittenRecords);
    }

    [Fact]
    public async Task RunAsync_PropagatesBrowserLaunchException_AbortingTheEntireRunImmediately()
    {
        var fetcher = new FakeListingFetcher((_, _) => throw new BrowserLaunchException("no chromium", new InvalidOperationException()));
        var parser = new FakeListingPageParser(_ => null);
        var writer = new FakeCsvFeedWriter();
        var summary = new RunSummary();
        var orchestrator = new ScrapeOrchestrator(
            fetcher, [parser], new ProductDeduper(), writer, Logger(), summary, TimeSpan.Zero);

        await Assert.ThrowsAsync<BrowserLaunchException>(() =>
            orchestrator.RunAsync(new ScrapeOptions([SeedUrl], "out.csv", MaxPages: null, DelayMs: 0)));

        Assert.Equal(1, fetcher.FetchedUrls.Count); // never retried as an ordinary page failure
    }

    [Fact]
    public async Task RunAsync_TriesParsersInInjectedOrder_UsingFirstNonNullResult()
    {
        var fetcher = new FakeListingFetcher((url, _) => PageContent(url));
        var firstParserCalled = false;
        var secondParserCalled = false;
        var firstParser = new FakeListingPageParser(_ => { firstParserCalled = true; return null; });
        var secondParser = new FakeListingPageParser(_ => { secondParserCalled = true; return new ParsedPage([Product("P1")], HasNextPage: false); });
        var writer = new FakeCsvFeedWriter();
        var summary = new RunSummary();
        var orchestrator = new ScrapeOrchestrator(
            fetcher, [firstParser, secondParser], new ProductDeduper(), writer, Logger(), summary, TimeSpan.Zero);

        await orchestrator.RunAsync(new ScrapeOptions([SeedUrl], "out.csv", MaxPages: null, DelayMs: 0));

        Assert.True(firstParserCalled);
        Assert.True(secondParserCalled);
        Assert.Single(writer.WrittenRecords);
    }
}
