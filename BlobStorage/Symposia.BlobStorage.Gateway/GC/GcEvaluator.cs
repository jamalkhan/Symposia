using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.GC;

/// <summary>
/// Finds garbage that has become eligible and queues it (#16). Evaluators never delete: they enqueue, and
/// <see cref="GcWorker"/> deletes through the safety gate. Covered here: soft-deleted versions past their
/// bucket's recovery window (FR2.1), versions a lifecycle expires (FR3.1, minimum retention honoured, FR3.2),
/// and delete markers with nothing beneath them (FR3.3). Multipart TTLs (FR1) run in MultipartReaper; stale
/// replicas (FR4) in ReplicationMonitor; orphans (FR5) in NodeReconciler.
/// </summary>
public sealed class GcEvaluator(GatewayMetadataStore store, IOptions<GatewayOptions> options, ILogger<GcEvaluator> logger) : BackgroundService
{
    private const int Batch = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.Gc.EvaluateIntervalSeconds)));
        do
        {
            try
            {
                Evaluate(DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "GC evaluation failed; retrying next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One evaluation pass; returns (versions expired, versions queued for purge, markers removed).</summary>
    public (int Expired, int Queued, int Markers) Evaluate(DateTimeOffset now)
    {
        var expired = 0;
        foreach (var bucket in store.VersionedBuckets())
        {
            foreach (var version in store.ExpiredVersions(bucket, store.Lifecycle(bucket), now))
            {
                store.SoftDeleteVersion(version.Id);
                expired++;
            }
        }

        // Each bucket's own recovery window is applied by the query.
        var queued = 0;
        foreach (var version in store.PurgeableVersions(now, options.Value.Gc.RecoveryDays, Batch))
        {
            store.QueueVersionPurge(version);
            queued++;
        }

        var markers = store.CleanOrphanedDeleteMarkers();
        if (expired + queued + markers > 0)
        {
            logger.LogInformation("GC evaluation: {Expired} version(s) expired, {Queued} queued for purge, {Markers} delete marker(s) removed.",
                expired, queued, markers);
        }

        return (expired, queued, markers);
    }
}
