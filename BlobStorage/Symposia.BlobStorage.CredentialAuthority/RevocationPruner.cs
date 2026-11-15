namespace Symposia.BlobStorage.CredentialAuthority;

/// <summary>Prunes revocation entries held past their holding period (#16 FR6.1) and change-log rows past retention (#245), daily.</summary>
public sealed class RevocationPruner(AuthorityStore store, ILogger<RevocationPruner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                if (await store.PruneRevocationsAsync(DateTimeOffset.UtcNow, stoppingToken) is var pruned and > 0)
                {
                    logger.LogInformation("Pruned {Count} expired revocation(s) past their holding period.", pruned);
                }

                if (await store.PruneChangeLogAsync(DateTimeOffset.UtcNow, stoppingToken) is var changes and > 0)
                {
                    logger.LogInformation("Pruned {Count} change-log row(s) past retention (#245).", changes);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Revocation pruning failed; retrying tomorrow.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
