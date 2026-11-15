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

    /// <summary>
    /// Every replica answered that it doesn't hold the blob (#8 FR7.2): the data is lost, not just
    /// unreachable. Nothing has been written to the response.
    /// </summary>
    Lost,
}

/// <summary>
/// The object operations both blob APIs share (#203): the S3 surface and the Symposia-native /v1
/// surface are thin adapters over these, so reads, writes, CID verification, read repair, deletion
/// and operation history (#204) behave identically whichever API a client uses. Each operation marks
/// its own history entry here, the one hook point, and the adapters only map results to XML or JSON.
/// </summary>
public sealed class ObjectService(GatewayMetadataStore store, INodeRegistry nodes, RepairQueue repairQueue, Quorum.QuorumWriter writer,
    Quorum.TenantQuota quota, Health.GatewayMetrics metrics, Erasure.ErasureObjects erasure, ILogger<ObjectService> logger)
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
        var tenantId = ctx.Items["TenantId"] as string ?? "";
        var expected = S3.UploadBody.ExpectedLength(ctx.Request);
        quota.EnsureRoom(tenantId, expected); // before placement or any node contact (#1 FR-2)
        var (record, replaced) = await writer.WriteObjectAsync(tenantId, bucket, key, contentType, body, ctx.RequestAborted, expected,
            Placement.RegionAssignmentResolver.FromHeader(ctx.Request));
        if (record is not null)
        {
            Fill(written, record, replaced);
            Health.RequestLog.SelectedNode(ctx, string.Join(',', record.NodeIds));
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
        // Soft delete (#16 FR2): unreadable at once, bytes kept for the recovery window, then purged by GC.
        var record = store.SoftDeleteObject(bucket, key);
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
        var total = isRange ? length : record.SizeBytes;
        var request = new ReadBlobRequest { Cid = record.Cid, Offset = offset, Length = isRange ? length : 0 };
        if (record.IsErasureCoded)
        {
            return await ServeErasureCodedAsync(record, isRange, offset, total, ctx, view);
        }

        // Try each replica (healthy ones first) until one starts streaming (#135); shared with Complete (#5).
        var replicas = ReplicaReader.Order(nodes, record.NodeIds);
        var notFound = 0;
        var opened = await ReplicaReader.OpenAsync(replicas, request, logger, ctx.RequestAborted,
            onNotFound: missing =>
            {
                notFound++;
                repairQueue.EnqueueMissingReplica(record, missing.Id, store, nodes);
            });
        if (opened?.Node is not { } node || opened.Call is not { } call)
        {
            // Every replica says the blob is gone: lost, not merely unreachable (#8 FR7.2).
            var lost = notFound == replicas.Count;
            logger.LogWarning("No replica of {Bucket}/{Key} (CID {Cid}) could be read{Lost}.", record.Bucket, record.Key, record.Cid,
                lost ? "; every replica reports it missing" : "");
            Health.RequestLog.Error(ctx, lost ? "replicas lost" : "no replica reachable");
            return lost ? ServeOutcome.Lost : ServeOutcome.NoReplica;
        }

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
        // A node failing mid-stream is not client-visible (#1 FR-17): the read resumes from the next
        // replica at the byte it reached, and the running hash continues across replicas. Only a CID
        // mismatch, detectable at end of stream once bytes are sent, aborts the connection so the
        // client SDK sees a broken transfer and retries.
        using var hasher = isRange ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.Id };
        var served = new List<string> { node.Id };
        long sent = 0;
        var more = opened.HasFirst;
        try
        {
            while (true)
            {
                try
                {
                    while (more)
                    {
                        var chunk = call.ResponseStream.Current;
                        hasher?.AppendData(chunk.Data.Span);
                        await ctx.Response.Body.WriteAsync(chunk.Data.Memory, ctx.RequestAborted);
                        sent += chunk.Data.Length;
                        if (view is not null)
                        {
                            view.Size += chunk.Data.Length; // bytes actually served (#204)
                        }

                        more = await call.ResponseStream.MoveNext(ctx.RequestAborted);
                    }

                    call.Dispose();
                    break;
                }
                catch (RpcException ex) when (!ctx.RequestAborted.IsCancellationRequested)
                {
                    call.Dispose();
                    metrics.NodeErrors.WithLabels(node.Id, ex.StatusCode.ToString()).Inc();
                    if (ex.StatusCode == StatusCode.NotFound)
                    {
                        logger.LogWarning("Node {Url} returned NotFound for CID {Cid} mid-stream; flagging for repair.", node.Url, record.Cid);
                        repairQueue.EnqueueMissingReplica(record, node.Id, store, nodes);
                    }
                    else
                    {
                        logger.LogWarning("Read of CID {Cid} from {Url} failed mid-stream ({Status}) after {Sent} bytes; failing over.",
                            record.Cid, node.Url, ex.StatusCode, sent);
                        node.MarkUnhealthy();
                    }

                    if (sent >= total)
                    {
                        break; // every byte arrived before the error: nothing to resume
                    }

                    var resume = new ReadBlobRequest { Cid = record.Cid, Offset = offset + sent, Length = total - sent };
                    var next = await ReplicaReader.OpenAsync(
                        replicas.Where(r => !tried.Contains(r.Id)).ToList(), resume, logger, ctx.RequestAborted,
                        onNotFound: missing => repairQueue.EnqueueMissingReplica(record, missing.Id, store, nodes));
                    if (next is null)
                    {
                        // Headers are already sent and no replica is left: the client gets a broken transfer and retries.
                        Health.RequestLog.Error(ctx, $"all replicas failed mid-stream after {sent} bytes");
                        ctx.Abort();
                        if (view is not null) view.Outcome = "Error";
                        return ServeOutcome.Served;
                    }

                    metrics.ReadFailovers.Inc();
                    (node, call, more) = (next.Node, next.Call, next.HasFirst);
                    tried.Add(node.Id);
                    served.Add(node.Id);
                }
            }
        }
        finally
        {
            call.Dispose(); // idempotent; covers a client abort mid-copy
        }

        Health.RequestLog.SelectedNode(ctx, string.Join(',', served));

        // Full-object CID verification (skipped for range requests; partial hash is meaningless).
        if (hasher is not null)
        {
            var actualHex = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!actualHex.Equals(record.Cid, StringComparison.Ordinal))
            {
                // With a failover the bad bytes may have come from any replica that served part of the
                // stream; each is challenged by repair rather than all evicted on suspicion.
                logger.LogError(
                    "CID mismatch on GET {Bucket}/{Key}: expected {Expected}, got {Actual}. " +
                    "Aborting connection and flagging node {Url} for repair.",
                    record.Bucket, record.Key, record.Cid, actualHex, node.Url);
                if (served.Count == 1)
                {
                    repairQueue.EnqueueCorruptReplica(record, node.Id, store, nodes);
                }
                else
                {
                    repairQueue.Enqueue(new RepairTask(record.Cid, null, RepairReason.CorruptReplica));
                }

                // Abort the underlying connection — the client receives a broken transfer and must retry.
                Health.RequestLog.Error(ctx, "CID mismatch");
                ctx.Abort();
                if (view is not null) view.Outcome = "Error";
            }
        }

        return ServeOutcome.Served;
    }

    /// <summary>An erasure-coded object, decoded from any k shards (#8 FR5); full reads are CID-checked as for copies.</summary>
    private async Task<ServeOutcome> ServeErasureCodedAsync(ObjectRecord record, bool isRange, long offset, long length, HttpContext ctx,
        History.PendingOperation? view)
    {
        using var hasher = isRange ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var started = false;
        try
        {
            var ok = await erasure.ReadAsync(record, offset, length, ctx.Response.Body, hasher, ctx.RequestAborted, () =>
            {
                started = true;
                ctx.Response.ContentType = record.ContentType;
                ctx.Response.Headers.ETag = $"\"{record.ETag}\"";
                ctx.Response.Headers.LastModified = record.LastModified.ToString("R");
                if (isRange)
                {
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers.ContentRange = $"bytes {offset}-{offset + length - 1}/{record.SizeBytes}";
                }

                ctx.Response.ContentLength = length;
            });
            if (!ok)
            {
                Health.RequestLog.Error(ctx, "fewer than k shards reachable");
                return ServeOutcome.NoReplica;
            }
        }
        catch (IOException ex) when (started && !ctx.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("Erasure-coded read of {Bucket}/{Key} lost too many shards mid-stream: {Message}", record.Bucket, record.Key, ex.Message);
            ctx.Abort();
            repairQueue.Enqueue(new RepairTask(record.Cid, null, RepairReason.MissingReplica) { Priority = RepairPriority.P0 });
            return ServeOutcome.Served;
        }

        if (view is not null)
        {
            (view.Size, view.Etag, view.Cid) = (length, record.ETag, record.Cid);
        }

        if (hasher is not null && !Convert.ToHexStringLower(hasher.GetHashAndReset()).Equals(record.Cid, StringComparison.Ordinal))
        {
            logger.LogError("CID mismatch decoding {Bucket}/{Key}; aborting and repairing its shards.", record.Bucket, record.Key);
            repairQueue.Enqueue(new RepairTask(record.Cid, null, RepairReason.CorruptReplica));
            Health.RequestLog.Error(ctx, "CID mismatch");
            ctx.Abort();
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
