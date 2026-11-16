using System.Security.Cryptography;
using Symposia.BlobStorage.Domain;

namespace Symposia.BlobStorage.StorageNode.Storage;

/// <summary>
/// Rebuilds a lost or corrupt manifest from the blobs actually on disk (#12 FR1.4, AC2). Runs before the node
/// serves anything, so neither gossip nor the next epoch root ever reports a blob the node can't prove it holds:
/// each file is re-hashed, and only those matching their CID come back, as Active. A blob that reached disk but
/// whose manifest commit was lost in the crash comes back too, and its rebuilt row is a change-feed addition.
/// </summary>
/// <remarks>
/// Who referred to each blob isn't stored on disk, so rebuilt rows have no references until a gateway writes
/// the blob again; the epoch root attests them under tenant "". Files that fail their hash are left in place
/// for the disk orphan pass and gateway reconciliation; this node never deletes data on its own (#16 FR5.3).
/// </remarks>
public static class ManifestRecovery
{
    public sealed record Result(int Recovered, int Rejected);

    public static Result RebuildIfNeeded(ManifestStore manifest, LocalBlobStore blobs, ILogger logger)
    {
        if (!manifest.NeedsRebuild)
        {
            return new Result(0, 0);
        }

        if (manifest.QuarantinedPath is { } aside)
        {
            logger.LogError("Manifest database failed its integrity check; moved it to {Path} and rebuilding from disk.", aside);
        }

        int recovered = 0, rejected = 0;
        foreach (var cid in blobs.EnumerateCids())
        {
            if (Verify(blobs, cid))
            {
                manifest.Upsert(new BlobRecord(cid, blobs.GetLength(cid), "", "", "", [], blobs.WrittenAt(cid), DateTimeOffset.UtcNow, BlobStatus.Active));
                recovered++;
            }
            else
            {
                logger.LogWarning("Blob {Cid} on disk doesn't match its CID; not restoring it to the manifest.", cid);
                rejected++;
            }
        }

        if (recovered > 0 || rejected > 0 || manifest.QuarantinedPath is not null)
        {
            logger.LogWarning("manifest_rebuilt: {Recovered} blob(s) verified and restored, {Rejected} rejected.", recovered, rejected);
        }

        return new Result(recovered, rejected);
    }

    private static bool Verify(LocalBlobStore blobs, Cid cid)
    {
        try
        {
            using var stream = blobs.OpenRead(cid);
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == cid.Value;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
