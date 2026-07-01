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
/// 2. Disk pass — enumerate all blob files on disk.  Any file whose CID is not in the manifest
///    (i.e. no metadata was ever committed, or the manifest row was deleted before the file) is
///    immediately deleted.  These are genuine write-orphans — no client can ever request them.
/// </summary>
public sealed class IntegritySelfCheckWorker : BackgroundService
{
    private const int ChunkSizeBytes = 64 * 1024;

    private readonly LocalBlobStore _blobStore;
    private readonly ManifestStore _manifest;
    private readonly IOptions<StorageNodeOptions> _options;
    private readonly ILogger<IntegritySelfCheckWorker> _logger;

    public IntegritySelfCheckWorker(
        LocalBlobStore blobStore,
        ManifestStore manifest,
        IOptions<StorageNodeOptions> options,
        ILogger<IntegritySelfCheckWorker> logger)
    {
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

            var interval = TimeSpan.FromSeconds(_options.Value.IntegrityCheckIntervalSeconds);
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
                corrupt++;
            }

            // Yield between blobs so we don't peg the CPU/disk during live traffic.
            await Task.Yield();
        }

        _logger.LogInformation(
            "Integrity check complete: {Ok} ok, {Corrupt} corrupt, {Missing} missing-from-disk.",
            ok, corrupt, missing);
    }

    // ── Pass 2: remove disk blobs with no manifest entry ─────────────────────

    private void RunDiskOrphanPass()
    {
        int purged = 0;
        foreach (var cid in _blobStore.EnumerateCids())
        {
            if (_manifest.Get(cid) is null)
            {
                _logger.LogInformation("Purging disk-orphan blob {Cid} (no manifest entry).", cid);
                _blobStore.Delete(cid);
                purged++;
            }
        }

        if (purged > 0)
            _logger.LogInformation("Disk orphan pass purged {Count} blobs.", purged);
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
