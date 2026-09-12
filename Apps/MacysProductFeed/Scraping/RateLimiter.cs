namespace MacysProductFeed.Scraping;

/// <summary>
/// Centralizes the --delay-ms throttle (issue #127 FR-7) so every navigation
/// call site enforces it consistently rather than each remembering to sleep.
/// Processing is strictly sequential across all seed URLs, so this is the
/// sole, process-wide request-pacing mechanism.
/// </summary>
public sealed class RateLimiter(int delayMs)
{
    public int DelayMs { get; } = delayMs;

    public Task WaitAsync(CancellationToken cancellationToken = default) =>
        DelayMs <= 0 ? Task.CompletedTask : Task.Delay(DelayMs, cancellationToken);
}
