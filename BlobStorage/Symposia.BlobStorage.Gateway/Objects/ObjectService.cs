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

    /// <summary>No such object; nothing has been written to the response.</summary>
    NotFound,
}

/// <summary>
/// The object operations both blob APIs share (#203): the S3 surface and the Symposia-native /v1
/// surface are thin adapters over these, so reads, writes, CID verification, read repair, deletion
/// and operation history (#204) behave identically whichever API a client uses. Each operation marks
/// its own history entry here, the one hook point, and the adapters only map results to XML or JSON.
/// </summary>
public sealed class ObjectService(GatewayMetadataStore store, INodeRegistry nodes, RepairQueue repairQueue, Quorum.QuorumWriter writer,
    ILogger<ObjectService> logger)
{
    /// <summary>Reads an object (or a range of it) into the response, recording a view.</summary>
    public Task<ServeOutcome> GetAsync(HttpContext ctx, string bucket, string key)
    {
        var view = History.HistoryCapture.Mark(ctx, History.HistoryOperation.View, bucket, key, History.HistoryCapture.ApiOf(ctx));
        return store.GetObject(bucket, key) is { } record
            ? ServeAsync(record, ctx.Request.Headers.Range.FirstOrDefault(), ctx, view)
            : Task.FromResult(ServeOutcome.NotFound);
    }

    /// <summary>
    /// Writes <paramref name="body"/> through the quorum writer, recording an insert or an update as
    /// decided inside the commit's own transaction. Null when write quorum wasn't reached; rejected
    /// bodies throw as they stream, before anything is committed.
    /// </summary>
    public async Task<ObjectRecord?> PutAsync(HttpContext ctx, string bucket, string key, string contentType, Stream body)
    {
        // Labelled from a pre-read only in case the write fails; a commit relabels it from the truth.
        var written = History.HistoryCapture.Mark(ctx, store.GetObject(bucket, key) is null ? History.HistoryOperation.Insert : History.HistoryOperation.Update,
            bucket, key, History.HistoryCapture.ApiOf(ctx));
        var (record, replaced) = await writer.WriteObjectAsync(ctx.Items["TenantId"] as string ?? "", bucket, key, contentType, body, ctx.RequestAborted,
            S3.UploadBody.ExpectedLength(ctx.Request));
        if (record is not null)
        {
            Fill(written, record, replaced);
        }

        return record;
    }

    /// <summary>Copies an object's metadata pointer (content-addressed: no data moves), recording a view of the source and a write of the destination.</summary>
    public ObjectRecord? Copy(HttpContext ctx, string sourceBucket, string sourceKey, string bucket, string key)
    {
        var api = History.HistoryCapture.ApiOf(ctx);
        var read = History.HistoryCapture.Mark(ctx, History.HistoryOperation.View, sourceBucket, sourceKey, api);
        if (store.GetObject(sourceBucket, sourceKey) is not { } source)
        {
            return null;
        }

        (read.Size, read.Etag, read.Cid) = (source.SizeBytes, source.ETag, source.Cid);
        var written = History.HistoryCapture.Mark(ctx, History.HistoryOperation.Insert, bucket, key, api);
        var copy = source with { Bucket = bucket, Key = key, LastModified = DateTimeOffset.UtcNow };
        Fill(written, copy, store.PutObject(copy));
        return copy;
    }

    /// <summary>Deletes an object, recording the delete (outcome NotFound when it was already absent; deletes stay idempotent).</summary>
    public ObjectRecord? Delete(HttpContext ctx, string bucket, string key)
    {
        var deleted = History.HistoryCapture.Mark(ctx, History.HistoryOperation.Delete, bucket, key, History.HistoryCapture.ApiOf(ctx));
        var record = store.DeleteObjectAndCollect(bucket, key);
        if (record is null)
        {
            deleted.Outcome = "NotFound";
        }
        else
        {
            (deleted.Size, deleted.Etag, deleted.Cid) = (record.SizeBytes, record.ETag, record.Cid);
        }

        return record;
    }

    private static void Fill(History.PendingOperation op, ObjectRecord record, bool replaced)
    {
        op.Operation = replaced ? History.HistoryOperation.Update : History.HistoryOperation.Insert;
        (op.Size, op.Etag, op.Cid) = (record.SizeBytes, record.ETag, record.Cid);
    }

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
            (view.Size, view.Etag, view.Cid) = (0, record.ETag, record.Cid);
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
                if (view is not null)
                {
                    view.Size += chunk.Data.Length; // bytes actually served (#204)
                }

                more = await call.ResponseStream.MoveNext(ctx.RequestAborted);
            }
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.NotFound && !ctx.RequestAborted.IsCancellationRequested)
        {
            // Headers are already sent: the client gets a broken transfer and retries.
            logger.LogWarning("Read of CID {Cid} from {Url} failed mid-stream ({Status}); aborting.", record.Cid, node.Url, ex.StatusCode);
            node.MarkUnhealthy();
            ctx.Abort();
            if (view is not null) view.Outcome = "Error";
            return ServeOutcome.Served;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // Node claims it doesn't have the blob — it's missing from this replica.
            logger.LogWarning("Node {Url} returned NotFound for CID {Cid}; flagging for repair.", node.Url, record.Cid);
            repairQueue.EnqueueMissingReplica(record, node.Id, store, nodes);
            ctx.Abort();
            if (view is not null) view.Outcome = "Error";
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
                if (view is not null) view.Outcome = "Error";
            }
        }

        return ServeOutcome.Served;
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
