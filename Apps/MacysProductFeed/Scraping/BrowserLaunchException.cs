namespace MacysProductFeed.Scraping;

/// <summary>
/// Thrown when the headless browser fails to launch. This is a fatal,
/// run-aborting condition (Arch plan: "no partial run is possible without a
/// browser") — the orchestrator must let it propagate rather than treating
/// it as a retryable per-page failure.
/// </summary>
public sealed class BrowserLaunchException(string message, Exception innerException)
    : Exception(message, innerException);
