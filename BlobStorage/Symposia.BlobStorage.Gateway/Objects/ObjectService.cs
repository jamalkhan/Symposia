using System.Security.Cryptography;
using Grpc.Core;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Repair;
using Symposia.BlobStorage.Gateway.S3;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Objects;

public enum ServeOutcome
{
    /// <summary>The body was streamed (or the connection aborted after a mid-stream failure, which the client retries).</summary>
    Served,

    /// <summary>No replica could be opened; nothing has been written to the response.</summary>
    NoReplica,
}

/// <summary>
/// The object operations both blob APIs share (#203): the S3 surface and the Symposia-native /v1
/// surface are thin adapters over these, so reads, CID verification, read repair and deletion
/// behave identically whichever API a client uses. Writes already go through <see cref="Quorum.QuorumWriter"/>.
/// </summary>
public sealed class ObjectService(GatewayMetadataStore store, INodeRegistry nodes, RepairQueue repairQueue, ILogger<ObjectService> logger)
{
    /// <summary>
    /// Streams <paramref name="record"/> (or the requested range) to the response. Headers are set
    /// only once a replica is streaming, so on <see cref="ServeOutcome.NoReplica"/> the caller can
    /// still answer with its own error format.
    /// </summary>
    public async Task<ServeOutcome> ServeAsync(ObjectRecord record, string? rangeHeader, HttpContext ctx, History.PendingOperation? view = null)
    {
        long offset = 0, length = 0;
        var isRange = rangeHeader is not null && TryParseRange(rangeHeader, record.SizeBytes, out offset, out length);
        var request = new ReadBlobRequest { Cid = record.Cid, Offset = offset, Length = isRange ? length : 0 };

        // Try each replica (healthy ones first) until one starts streaming (#135); shared with Complete (#5).
        var opened = await ReplicaReader.OpenAsync(ReplicaReader.Order(nodes, record.NodeIds), request, logger, ctx.RequestAborted,
            onNotFound: missing => repairQueue.EnqueueMissingReplica(record, missing.Id, store, nodes));
        if (opened?.Node is not { } node || opened.Call is not { } call)
        {
            logger.LogWarning("No replica of {Bucket}/{Key} (CID {Cid}) could be read.", record.Bucket, record.Key, record.Cid);
            return ServeOutcome.NoReplica;
        }

        using var activeCall = call;
        if (view is not null)
        {
            (view.Size, view.Etag, view.Cid) = (isRange ? length : record.SizeBytes, record.ETag, record.Cid);
        }

        ctx.Response.ContentType = record.ContentType;
        ctx.Response.Headers.ETag = $"\"{record.ETag}\"";
        ctx.Response.Headers.LastModified = record.LastModified.ToString("R");
        if (isRange)
        {
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers.ContentRange = $"bytes {offset}-{offset + length - 1}/{record.SizeBytes}";
            ctx.Response.ContentLength = length;
        }
        else
        {
            ctx.Response.ContentLength = record.SizeBytes;
        }

        // Stream to client while computing SHA-256 for CID verification.
        // Per Requirements/BlobStorage/redundancy-and-data-integrity.md: a read returning data
        // that doesn't match the stored CID fails immediately and triggers read repair.
        // We can only detect the mismatch at end-of-stream; at that point we abort the connection
        // so the client SDK sees a broken transfer and retries. The corrupt replica is then
        // flagged for removal and re-replication.
        using var hasher = isRange ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var more = opened.HasFirst;
            while (more)
            {
                var chunk = call.ResponseStream.Current;
                hasher?.AppendData(chunk.Data.Span);
                await ctx.Response.Body.WriteAsync(chunk.Data.Memory, ctx.RequestAborted);
                more = await call.ResponseStream.MoveNext(ctx.RequestAborted);
            }
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.NotFound && !ctx.RequestAborted.IsCancellationRequested)
        {
            // Headers are already sent: the client gets a broken transfer and retries.
            logger.LogWarning("Read of CID {Cid} from {Url} failed mid-stream ({Status}); aborting.", record.Cid, node.Url, ex.StatusCode);
            node.MarkUnhealthy();
            ctx.Abort();
            return ServeOutcome.Served;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // Node claims it doesn't have the blob — it's missing from this replica.
            logger.LogWarning("Node {Url} returned NotFound for CID {Cid}; flagging for repair.", node.Url, record.Cid);
            repairQueue.EnqueueMissingReplica(record, node.Id, store, nodes);
            ctx.Abort();
            return ServeOutcome.Served;
        }

        // Full-object CID verification (skipped for range requests; partial hash is meaningless).
        if (hasher is not null)
        {
            var actualHex = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!actualHex.Equals(record.Cid, StringComparison.Ordinal))
            {
                logger.LogError(
                    "CID mismatch on GET {Bucket}/{Key}: expected {Expected}, got {Actual}. " +
                    "Aborting connection and flagging node {Url} for repair.",
                    record.Bucket, record.Key, record.Cid, actualHex, node.Url);
                repairQueue.EnqueueCorruptReplica(record, node.Id, store, nodes);
                // Abort the underlying connection — the client receives a broken transfer and must retry.
                ctx.Abort();
            }
        }

        return ServeOutcome.Served;
    }

    /// <summary>
    /// Removes the object from the index at once, then deletes its blob from each node when no other
    /// object shares the CID (failures are queued for <c>GcWorker</c>). Returns the removed record, or null.
    /// </summary>
    public ObjectRecord? Delete(string bucket, string key)
    {
        var record = store.GetObject(bucket, key);
        store.DeleteObject(bucket, key);
        if (record is not null && !store.IsCidReferenced(record.Cid))
        {
            foreach (var nodeUrl in record.NodeIds)
            {
                var node = nodes.All.FirstOrDefault(n => n.Id.Equals(nodeUrl, StringComparison.OrdinalIgnoreCase));
                if (node is not null)
                    _ = DeleteFromNodeAsync(node, record.Cid);
                else
                    store.EnqueueNodeDeletion(record.Cid, nodeUrl);
            }
        }

        return record;
    }

    private async Task DeleteFromNodeAsync(NodeConnection node, string cid)
    {
        try
        {
            await node.Client.DeleteBlobAsync(new DeleteBlobRequest { Cid = cid });
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to delete blob {Cid} from node {Url}: {Message}. Queued for retry.", cid, node.Url, ex.Message);
            store.EnqueueNodeDeletion(cid, node.Id);
        }
    }

    private static bool TryParseRange(string header, long totalBytes, out long offset, out long length)
    {
        offset = 0;
        length = 0;
        // Expect: "bytes=start-end"
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = header["bytes=".Length..].Split('-');
        if (parts.Length != 2) return false;

        long start = 0, end = totalBytes - 1;
        if (!string.IsNullOrEmpty(parts[0]) && !long.TryParse(parts[0], out start)) return false;
        if (!string.IsNullOrEmpty(parts[1]) && !long.TryParse(parts[1], out end)) return false;

        if (start > end || end >= totalBytes) return false;

        offset = start;
        length = end - start + 1;
        return true;
    }
}
