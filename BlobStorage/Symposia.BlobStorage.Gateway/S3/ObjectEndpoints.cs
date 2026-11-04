using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Quorum;
using Symposia.BlobStorage.Gateway.Repair;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.S3;

internal static class ObjectEndpoints
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        // PutObject / CopyObject (x-amz-copy-source present → copy path)
        app.MapPut("/{bucket}/{**key}", PutOrCopy);

        // GetObject
        app.MapGet("/{bucket}/{**key}", GetObject);

        // HeadObject
        app.MapMethods("/{bucket}/{**key}", ["HEAD"], HeadObject);

        // DeleteObject
        app.MapDelete("/{bucket}/{**key}", DeleteObject);
    }

    // ── PutObject / CopyObject ────────────────────────────────────────────────

    private static async Task<IResult> PutOrCopy(
        string bucket, string key, HttpContext ctx,
        GatewayMetadataStore store, QuorumWriter writer)
    {
        if (!TenantId(ctx, out var tenantId)) return S3Xml.AccessDenied();

        // x-amz-copy-source header triggers CopyObject semantics.
        var copySource = ctx.Request.Headers["x-amz-copy-source"].FirstOrDefault();
        if (copySource is not null)
            return CopyObject(bucket, key, copySource, store);

        if (!store.BucketExists(bucket))
            return S3Xml.NoSuchBucket(bucket);

        var contentType = ctx.Request.ContentType ?? "application/octet-stream";

        // Object bytes from the wire format (aws-chunked, #135), with chunk signatures and every
        // declared checksum verified as they stream (#139). A failed check throws before the body
        // ends, so the node writes are cancelled and nothing is committed.
        ObjectRecord? record;
        ChecksumVerifyingStream body;
        try
        {
            body = UploadBody.Open(ctx);
            record = await writer.WriteAsync(tenantId, bucket, key, contentType, body, ctx.RequestAborted);
        }
        catch (InvalidDataException ex)
        {
            return S3Xml.IncompleteBody(ex.Message);
        }
        catch (S3RequestRejectedException ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Symposia.BlobStorage.Gateway.S3.Upload")
                .LogWarning("Rejected PUT {Bucket}/{Key} from {AccessKey}: {Code} {Message}", bucket, key,
                    (ctx.Items[nameof(Auth.SigV4Context)] as Auth.SigV4Context)?.AccessKeyId, ex.Code, ex.Message);
            return S3Xml.Error(ex.Status, ex.Code, ex.Message);
        }

        foreach (var (algorithm, value) in body.Computed.Where(c => c.Key != "md5"))
        {
            ctx.Response.Headers[$"x-amz-checksum-{algorithm}"] = value;
        }

        if (record is null)
            return S3Xml.ServiceUnavailable();

        ctx.Response.Headers.ETag = $"\"{record.Cid}\"";
        return Results.Ok();
    }

    // CopyObject: content-addressed store means no data movement.
    // We simply copy the metadata pointer (the CID is identical if content is identical,
    // which is the expected semantics). Different key → new metadata entry, same CID.
    private static IResult CopyObject(
        string destBucket, string destKey, string copySource, GatewayMetadataStore store)
    {
        // copy-source format: /bucket/key or bucket/key (AWS SDK v4 percent-encodes slashes)
        // URL-decode first so %2F-encoded slashes become real separators before we split.
        var path = Uri.UnescapeDataString(copySource.TrimStart('/'));
        var slash = path.IndexOf('/');
        if (slash < 0) return S3Xml.BadRequest("Invalid x-amz-copy-source header.");

        var srcBucket = path[..slash];
        var srcKey = path[(slash + 1)..];

        var src = store.GetObject(srcBucket, srcKey);
        if (src is null) return S3Xml.NoSuchKey(srcKey);

        var dest = src with { Bucket = destBucket, Key = destKey, LastModified = DateTimeOffset.UtcNow };
        store.PutObject(dest);

        return S3Xml.CopyObjectResponse(dest.Cid, dest.LastModified);
    }

    // ── GetObject ─────────────────────────────────────────────────────────────

    private static async Task<IResult> GetObject(
        string bucket, string key, HttpContext ctx,
        GatewayMetadataStore store, INodeRegistry nodes,
        ILogger<Program> logger,
        RepairQueue repairQueue)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();

        var record = store.GetObject(bucket, key);
        if (record is null) return S3Xml.NoSuchKey(key);

        // Parse Range header: "bytes=start-end"
        long offset = 0;
        long length = 0;
        var isRange = false;
        if (ctx.Request.Headers.Range.FirstOrDefault() is { } rangeHeader)
        {
            isRange = TryParseRange(rangeHeader, record.SizeBytes, out offset, out length);
        }

        var request = new ReadBlobRequest
        {
            Cid = record.Cid,
            Offset = offset,
            Length = isRange ? length : 0,
        };

        // Try each replica (healthy ones first) until one starts streaming. Nothing is sent to the
        // client until the first chunk arrives, so a replica on a node that just went down (and
        // isn't yet marked unhealthy by a probe) costs a retry, not a failed GET (issue #135 AC3).
        var replicas = nodes.All
            .Where(n => record.NodeIds.Contains(n.Id, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(n => n.IsHealthy)
            .ThenByDescending(n => n.LastProbeTime)
            .ToList();

        NodeConnection? node = null;
        AsyncServerStreamingCall<ReadBlobChunk>? call = null;
        var hasFirst = false;
        foreach (var candidate in replicas)
        {
            var attempt = candidate.Client.ReadBlob(request, deadline: DateTime.UtcNow.AddMinutes(30), cancellationToken: ctx.RequestAborted);
            // A hung node must not stall the GET before a healthy replica is tried.
            using var firstChunk = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            firstChunk.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                hasFirst = await attempt.ResponseStream.MoveNext(firstChunk.Token);
                node = candidate;
                call = attempt;
                break;
            }
            catch (RpcException ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                attempt.Dispose();
                if (ex.StatusCode == StatusCode.NotFound)
                {
                    logger.LogWarning("Node {Url} returned NotFound for CID {Cid}; flagging for repair and trying another replica.", candidate.Url, record.Cid);
                    repairQueue.EnqueueMissingReplica(record, candidate.Id, store, nodes);
                }
                else
                {
                    // Treat it as down until its next successful probe so other requests skip it.
                    logger.LogWarning("Read of CID {Cid} from {Url} failed ({Status}); trying another replica.", record.Cid, candidate.Url, ex.StatusCode);
                    candidate.MarkUnhealthy();
                }
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                attempt.Dispose();
                logger.LogWarning("Read of CID {Cid} from {Url} sent nothing for 10 s; trying another replica.", record.Cid, candidate.Url);
                candidate.MarkUnhealthy();
            }
        }

        if (node is null || call is null)
        {
            logger.LogWarning("No replica of {Bucket}/{Key} (CID {Cid}) could be read.", bucket, key, record.Cid);
            return S3Xml.ServiceUnavailable();
        }

        using var activeCall = call;
        ctx.Response.ContentType = record.ContentType;
        ctx.Response.Headers.ETag = $"\"{record.Cid}\"";
        ctx.Response.Headers.LastModified = record.LastModified.ToString("R");

        if (isRange)
        {
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers.ContentRange =
                $"bytes {offset}-{offset + length - 1}/{record.SizeBytes}";
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
            var more = hasFirst;
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
            return Results.Empty;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // Node claims it doesn't have the blob — it's missing from this replica.
            logger.LogWarning("Node {Url} returned NotFound for CID {Cid}; flagging for repair.", node.Url, record.Cid);
            repairQueue.EnqueueMissingReplica(record, node.Id, store, nodes);
            ctx.Abort();
            return Results.Empty;
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
                    bucket, key, record.Cid, actualHex, node.Url);

                repairQueue.EnqueueCorruptReplica(record, node.Id, store, nodes);
                // Abort the underlying connection — the client receives a broken transfer and must retry.
                ctx.Abort();
                return Results.Empty;
            }
        }

        return Results.Empty;
    }

    // ── HeadObject ────────────────────────────────────────────────────────────

    private static IResult HeadObject(
        string bucket, string key, HttpContext ctx, GatewayMetadataStore store)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();

        var record = store.GetObject(bucket, key);
        if (record is null) return S3Xml.NoSuchKey(key);

        ctx.Response.Headers.ETag = $"\"{record.Cid}\"";
        ctx.Response.Headers.LastModified = record.LastModified.ToString("R");
        ctx.Response.ContentType = record.ContentType;
        ctx.Response.ContentLength = record.SizeBytes;
        return Results.Ok();
    }

    // ── DeleteObject ──────────────────────────────────────────────────────────

    private static async Task<IResult> DeleteObject(
        string bucket, string key, HttpContext ctx,
        GatewayMetadataStore store, INodeRegistry nodes, ILogger<Program> logger)
    {
        if (!TenantId(ctx, out _)) return S3Xml.AccessDenied();

        var record = store.GetObject(bucket, key);

        // Remove from metadata immediately (makes object invisible to LIST/GET).
        store.DeleteObject(bucket, key);

        if (record is not null && !store.IsCidReferenced(record.Cid))
        {
            // No other object shares this CID — attempt to delete the blob from each node.
            // Fire-and-forget: failures are queued in gc_queue for retry by GcWorker.
            foreach (var nodeUrl in record.NodeIds)
            {
                var node = nodes.All.FirstOrDefault(n =>
                    n.Id.Equals(nodeUrl, StringComparison.OrdinalIgnoreCase));

                if (node is not null)
                    _ = DeleteFromNodeAsync(node, record.Cid, store, logger);
                else
                    store.EnqueueNodeDeletion(record.Cid, nodeUrl);
            }
        }

        // S3 DeleteObject is always 204, even if the key did not exist.
        return Results.StatusCode(204);
    }

    private static async Task DeleteFromNodeAsync(
        NodeConnection node, string cid, GatewayMetadataStore store, ILogger logger)
    {
        try
        {
            await node.Client.DeleteBlobAsync(new DeleteBlobRequest { Cid = cid });
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Failed to delete blob {Cid} from node {Url}: {Message}. Queued for retry.",
                cid, node.Url, ex.Message);
            store.EnqueueNodeDeletion(cid, node.Id);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TenantId(HttpContext ctx, out string tenantId)
    {
        tenantId = ctx.Items["TenantId"] as string ?? "";
        return tenantId.Length > 0;
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
