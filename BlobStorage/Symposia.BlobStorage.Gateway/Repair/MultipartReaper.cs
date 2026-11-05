using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.Repair;

/// <summary>
/// Periodic multipart housekeeping (#5 review): releases Completing leases left by a gateway that
/// stopped mid-assembly, aborts expired uploads so their parts stop pinning storage, and purges
/// finished upload rows past the retention window. Runs once at startup, then every interval.
/// </summary>
public sealed class MultipartReaper(GatewayMetadataStore store, IOptions<GatewayOptions> options, ILogger<MultipartReaper> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, o.MultipartReapIntervalSeconds)));
        do
        {
            try
            {
                var (released, expired, purged) = store.ReapUploads(DateTimeOffset.UtcNow, o.CompletingLease, TimeSpan.FromDays(o.MultipartRetentionDays));
                if (released + expired + purged > 0)
                {
                    logger.LogInformation(
                        "Multipart housekeeping: {Released} stale Completing lease(s) released, {Expired} expired upload(s) aborted, {Purged} finished row(s) purged.",
                        released, expired, purged);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Multipart housekeeping failed; retrying next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
