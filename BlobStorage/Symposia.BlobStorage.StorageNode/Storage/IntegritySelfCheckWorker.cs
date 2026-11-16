using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;

namespace Symposia.BlobStorage.StorageNode.Storage;

/// <summary>
/// Periodic local integrity verification and orphan scan.
///
/// Two passes run on each cycle (Requirements/BlobStorage/redundancy-and-data-integrity.md):
///
/// 1. Manifest pass — for every CID in the local manifest, hash the on-disk file and compare
///    against the CID.  A mismatch means the file is corrupt; it is marked Orphaned so the
///    gateway's ReplicationMonitor can trigger re-replication from a healthy node.
///
/// 2. Disk pass — enumerate all blob files on disk. A file whose CID is not in the manifest and is
///    older than the 48-hour orphan grace is recorded as Orphaned for the gateway's reconciliation
///    (#16 FR5); the node never deletes it on its own.
/// </summary>
public sealed class IntegritySelfCheckWorker : BackgroundService
{
    private const int ChunkSizeBytes = 64 * 1024;

    private readonly LocalBlobStore _blobStore;
    private readonly ManifestStore _manifest;
    private readonly IOptions<StorageNodeOptions> _options;
    private readonly ILogger<IntegritySelfCheckWorker> _logger;
    private readonly Health.NodeMetrics? _metrics;

    public IntegritySelfCheckWorker(
        LocalBlobStore blobStore,
        ManifestStore manifest,
        IOptions<StorageNodeOptions> options,
        ILogger<IntegritySelfCheckWorker> logger,
        Health.NodeMetrics? metrics = null)
    {
        _metrics = metrics;
        _blobStore = blobStore;
        _manifest = manifest;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so startup I/O doesn't compete with the first requests.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunManifestPassAsync(stoppingToken);
                RunDiskOrphanPass();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Integrity self-check encountered an unexpected error; will retry next cycle.");
            }

            // Randomized (#8 FR6.3): 50–150 % of the interval, so no one can predict when blobs are re-hashed.
            var interval = TimeSpan.FromSeconds(_options.Value.IntegrityCheckIntervalSeconds * (0.5 + Random.Shared.NextDouble()));
            await Task.Delay(interval, stoppingToken);
        }
    }

    // ── Pass 1: verify every manifest entry ──────────────────────────────────

    private async Task RunManifestPassAsync(CancellationToken ct)
    {
        var all = _manifest.ListByStatus(BlobStatus.Active);
        _logger.LogInformation("Integrity check: verifying {Count} active blobs.", all.Count);

        int ok = 0, corrupt = 0, missing = 0;

        foreach (var record in all)
        {
            ct.ThrowIfCancellationRequested();

            if (!_blobStore.Exists(record.Cid))
            {
                _logger.LogWarning("Blob {Cid} is in manifest but missing from disk; marking Orphaned.", record.Cid);
                _manifest.SetStatus(record.Cid, BlobStatus.Orphaned);
                _metrics?.IntegrityFailures.Inc();
                missing++;
                continue;
            }

            var actualHex = await ComputeHashAsync(record.Cid, ct);
            if (actualHex == record.Cid.Value)
            {
                _manifest.UpdateChecksumVerifiedAt(record.Cid, DateTimeOffset.UtcNow);
                ok++;
            }
            else
            {
                _logger.LogError(
                    "Blob {Cid} is CORRUPT (computed {Actual}); marking Orphaned for network repair.",
                    record.Cid, actualHex);
                _manifest.SetStatus(record.Cid, BlobStatus.Orphaned);
                _metrics?.IntegrityFailures.Inc();
                corrupt++;
            }

            // Yield between blobs so we don't peg the CPU/disk during live traffic.
            await Task.Yield();
        }

        _logger.LogInformation(
            "Integrity check complete: {Ok} ok, {Corrupt} corrupt, {Missing} missing-from-disk.",
            ok, corrupt, missing);
    }

    // ── Pass 2: disk blobs with no manifest entry ────────────────────────────

    /// <summary>How old an unlisted file must be before it's treated as an orphan (#16 FR5.2/5.4).</summary>
    public static TimeSpan OrphanGrace { get; set; } = TimeSpan.FromHours(48);

    /// <summary>
    /// A blob file with no manifest row may be a write whose manifest entry is still landing, so it is never
    /// touched before it is 48 hours old (#16 FR5.4). Past that it is recorded in the manifest as Orphaned,
    /// which lists it to the gateway's reconciliation. Deleting is the coordination layer's call (#16 FR5.3),
    /// not this node's, so nothing is deleted here.
    /// </summary>
    public int RunDiskOrphanPass()
    {
        int flagged = 0;
        foreach (var cid in _blobStore.EnumerateCids())
        {
            if (_manifest.Get(cid) is not null)
            {
                continue;
            }

            var age = DateTimeOffset.UtcNow - _blobStore.WrittenAt(cid);
            if (age < OrphanGrace)
            {
                continue;
            }

            _logger.LogInformation("Disk blob {Cid} has had no manifest entry for {Age}; recording it as an orphan.", cid, age);
            _manifest.Upsert(new BlobRecord(cid, _blobStore.GetLength(cid), "", "", "", [], DateTimeOffset.UtcNow, null, BlobStatus.Orphaned));
            flagged++;
        }

        if (flagged > 0)
            _logger.LogInformation("Disk orphan pass recorded {Count} orphan(s) for reconciliation.", flagged);
        return flagged;
    }

    // ── Hash helper ───────────────────────────────────────────────────────────

    private async Task<string> ComputeHashAsync(Cid cid, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = _blobStore.OpenRead(cid);
        var buffer = new byte[ChunkSizeBytes];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            hasher.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }
}
