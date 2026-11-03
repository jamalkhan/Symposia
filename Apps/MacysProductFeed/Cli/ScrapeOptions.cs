namespace MacysProductFeed.Cli;

public sealed record ScrapeOptions(
    IReadOnlyList<string> Urls,
    string OutputPath,
    int? MaxPages,
    int DelayMs)
{
    public const string DefaultOutputPath = "./macys-product-feed.csv";
    public const int DefaultDelayMs = 1000;
}
