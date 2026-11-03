using System.CommandLine;

namespace MacysProductFeed.Cli;

public sealed class CliValidationException(string message) : Exception(message);

public static class CliParser
{
    /// <summary>
    /// Parses and validates CLI arguments into <see cref="ScrapeOptions"/>.
    /// Throws <see cref="CliValidationException"/> for any invalid input
    /// (issue #127 FR-2's "at least one URL source is required", Scenario 5).
    /// --url and --url-file may be combined; the tool processes the union
    /// of both sources (post-huddle clarification).
    /// </summary>
    public static ScrapeOptions Parse(string[] args)
    {
        var urlOption = new Option<string[]>("--url") { AllowMultipleArgumentsPerToken = false };
        var urlFileOption = new Option<string?>("--url-file");
        var outputOption = new Option<string>("--output", () => ScrapeOptions.DefaultOutputPath);
        var maxPagesOption = new Option<int?>("--max-pages");
        var delayMsOption = new Option<int>("--delay-ms", () => ScrapeOptions.DefaultDelayMs);

        var rootCommand = new RootCommand("Scrapes macys.com product listings and generates a CSV product feed.")
        {
            urlOption, urlFileOption, outputOption, maxPagesOption, delayMsOption
        };

        ScrapeOptions? parsedOptions = null;
        CliValidationException? validationError = null;

        rootCommand.SetHandler(context =>
        {
            try
            {
                var urls = new List<string>(context.ParseResult.GetValueForOption(urlOption) ?? []);

                var urlFile = context.ParseResult.GetValueForOption(urlFileOption);
                if (urlFile is not null)
                {
                    urls.AddRange(ReadUrlFile(urlFile));
                }

                var distinctUrls = urls.Distinct().ToList();
                if (distinctUrls.Count == 0)
                {
                    throw new CliValidationException(
                        "No input URLs provided. Supply at least one --url or a --url-file with at least one URL.");
                }

                var maxPages = context.ParseResult.GetValueForOption(maxPagesOption);
                if (maxPages is < 1)
                {
                    throw new CliValidationException("--max-pages must be a positive number.");
                }

                var delayMs = context.ParseResult.GetValueForOption(delayMsOption);
                if (delayMs < 0)
                {
                    throw new CliValidationException("--delay-ms cannot be negative.");
                }

                parsedOptions = new ScrapeOptions(
                    Urls: distinctUrls,
                    OutputPath: context.ParseResult.GetValueForOption(outputOption) ?? ScrapeOptions.DefaultOutputPath,
                    MaxPages: maxPages,
                    DelayMs: delayMs);
            }
            catch (CliValidationException ex)
            {
                validationError = ex;
            }
        });

        rootCommand.Invoke(args);

        if (validationError is not null)
        {
            throw validationError;
        }

        return parsedOptions ?? throw new CliValidationException("Failed to parse command-line arguments.");
    }

    private static IEnumerable<string> ReadUrlFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new CliValidationException($"--url-file path does not exist: {path}");
        }

        try
        {
            return File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliValidationException($"Failed to read --url-file at {path}: {ex.Message}");
        }
    }
}
