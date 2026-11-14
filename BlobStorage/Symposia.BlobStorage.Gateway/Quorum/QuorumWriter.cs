using Google.Protobuf;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Protocol;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Quorum;

/// <summary>
/// Streams an incoming blob to all target nodes in parallel and enforces write quorum.
/// See Requirements/BlobStorage/gateway-architecture.md#write-path and
/// Requirements/BlobStorage/write-quorum-and-consistency.md.
/// </summary>
public sealed class QuorumWriter
{
    private const int ChunkSizeBytes = 64 * 1024;

    private readonly INodeRegistry _nodes;
    private readonly GatewayMetadataStore _metadata;
    private readonly int _quorumCount;
    private readonly int _minCopies;
    private readonly Repair.RepairQueue _repairQueue;
    private readonly Health.GatewayMetrics? _metrics;

    public QuorumWriter(INodeRegistry nodes, GatewayMetadataStore metadata, IOptions<GatewayOptions> options,
        Repair.RepairQueue repairQueue, Health.GatewayMetrics? metrics = null)
    {
        _metrics = metrics;
        _nodes = nodes;
        _metadata = metadata;
        _quorumCount = options.Value.WriteQuorumCount;
        _minCopies = options.Value.MinCopiesPerObject;
        _repairQueue = repairQueue;
    }

    /// <summary>
    /// Fans out the body stream to all healthy nodes, waits for quorum, commits metadata.
    /// Returns the committed ObjectRecord on success, null if quorum was not reached.
    /// </summary>
    public async Task<ObjectRecord?> WriteAsync(
        string tenantId, string bucket, string key, string contentType,
        Stream body, CancellationToken cancellationToken, long? expectedSize = null) =>
        (await WriteObjectAsync(tenantId, bucket, key, contentType, body, cancellationToken, expectedSize)).Record;

    /// <summary>As <see cref="WriteAsync"/>, also reporting whether the commit replaced an existing object (#204).</summary>
    public async Task<(ObjectRecord? Record, bool Replaced)> WriteObjectAsync(
        string tenantId, string bucket, string key, string contentType,
        Stream body, CancellationToken cancellationToken, long? expectedSize = null)
    {
        var blob = await WriteBlobAsync(tenantId, bucket, key, body, cancellationToken, expectedSize);
        if (blob is null)
            return (null, false);

        var record = new ObjectRecord(
            bucket, key, blob.Cid, blob.SizeBytes, contentType,
            DateTimeOffset.UtcNow, blob.NodeIds);

        var replaced = _metadata.PutObject(record);
        RepairIfShort(blob);
        return (record, replaced);
    }

    /// <summary>
    /// Quorum can be below the copy target (issue #135: a write succeeds on one node); queue the
    /// missing copies now rather than waiting for the next replication scan. Shared with multipart Complete.
    /// </summary>
    public void RepairIfShort(BlobWriteResult blob)
    {
        if (blob.NodeIds.Count < _minCopies)
        {
            _repairQueue.Enqueue(new Repair.RepairTask(blob.Cid, null, Repair.RepairReason.UnderReplicated));
        }
    }

    /// <summary>
    /// Stores a blob on the healthy nodes and waits for quorum, without creating an object (multipart
    /// parts, #5). Null if quorum was not reached. Throws (after cancelling node writes) if reading
    /// the body fails, e.g. a failed integrity check.
    /// </summary>
    /// <remarks>
    /// Targets only nodes that take writes (#199); a node that refuses as read-only mid-write is
    /// dropped from this write without being marked unhealthy, and repair tops up the copies. The
    /// body is streamed, not buffered, so a refused copy can't be resent elsewhere: if too few
    /// nodes accept, quorum fails and the caller answers 503 for the client to retry.
    /// </remarks>
    public async Task<BlobWriteResult?> WriteBlobAsync(
        string tenantId, string bucket, string key, Stream body, CancellationToken cancellationToken, long? expectedSize = null)
    {
        expectedSize ??= body.CanSeek ? body.Length - body.Position : null;
        var targetNodes = _nodes.Writable(expectedSize);
        if (targetNodes.Count == 0)
            return null;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var header = new WriteBlobHeader { TenantId = tenantId, Bucket = bucket, Key = key, ExpectedSizeBytes = expectedSize ?? 0 };
        var sessions = targetNodes.Select(n => new NodeWriteSession(n)).ToList();

        // Open streams and send header to all nodes.
        await Task.WhenAll(sessions.Select(s => s.SendHeaderAsync(header, cancellationToken)));

        // Fan-out: read one chunk from the HTTP body, write it to all active node streams in parallel.
        var buffer = new byte[ChunkSizeBytes];
        int read;
        try
        {
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                var chunk = new WriteBlobChunk { Data = ByteString.CopyFrom(buffer, 0, read) };
                await Task.WhenAll(sessions.Select(s => s.SendChunkAsync(chunk, cancellationToken)));
            }
        }
        catch
        {
            // A bad or truncated request body: cancel the node streams so nothing is committed.
            foreach (var session in sessions)
            {
                session.Abort();
            }

            throw;
        }

        // Complete all streams and collect results.
        await Task.WhenAll(sessions.Select(s => s.CompleteAsync()));

        foreach (var refused in sessions.Where(s => s.RefusedReadOnly))
        {
            refused.Node.MarkReadOnly();
        }

        foreach (var failed in sessions.Where(s => s.Response is null && !s.RefusedReadOnly))
        {
            _metrics?.NodeErrors.WithLabels(failed.Node.Id, "write").Inc();
        }

        var successes = sessions.Where(s => s.Response is not null).ToList();
        if (successes.Count < _quorumCount)
        {
            _metrics?.WriteQuorumFailed.Inc();
            return null; // Quorum not reached.
        }

        _metrics?.WriteQuorumReached.Inc();
        _metrics?.WriteFanoutDuration.Observe(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);

        return new BlobWriteResult(
            successes[0].Response!.Cid,
            successes[0].Response!.SizeBytes,
            successes.Select(s => s.Node.Id).ToList());
    }

    /// <summary>
    /// Wraps a single gRPC streaming call to one node.
    /// Failures are absorbed — the session simply becomes inactive.
    /// </summary>
    private sealed class NodeWriteSession
    {
        private global::Grpc.Core.AsyncClientStreamingCall<WriteBlobChunk, WriteBlobResponse> _call;
        private bool _active = true;
        private bool _aborted;

        public NodeWriteSession(NodeConnection node)
        {
            Node = node;
            _call = node.Client.WriteBlob();
        }

        public NodeConnection Node { get; }
        public WriteBlobResponse? Response { get; private set; }

        /// <summary>The node refused the write for disk space (#199), not a fault.</summary>
        public bool RefusedReadOnly { get; private set; }

        /// <summary>
        /// Opens the node's stream with the header, retrying once on a transient error (#1 FR-10) before
        /// the node counts as failed. Only the header can be retried: the body streams through unbuffered,
        /// so once data has been sent a failed node is out of this write and repair tops up its copy.
        /// </summary>
        public async Task SendHeaderAsync(WriteBlobHeader header, CancellationToken ct)
        {
            if (!_active) return;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _call.RequestStream.WriteAsync(new WriteBlobChunk { Header = header }, ct);
                    return;
                }
                catch (Exception ex) when (attempt == 0 && IsTransient(ex) && !ct.IsCancellationRequested)
                {
                    _call.Dispose();
                    _call = Node.Client.WriteBlob();
                }
                catch
                {
                    _active = false;
                    return;
                }
            }
        }

        private static bool IsTransient(Exception ex) => ex is global::Grpc.Core.RpcException
        {
            StatusCode: global::Grpc.Core.StatusCode.Unavailable or global::Grpc.Core.StatusCode.DeadlineExceeded,
        } or IOException or HttpRequestException;

        public async Task SendChunkAsync(WriteBlobChunk chunk, CancellationToken ct)
        {
            if (!_active) return;
            try { await _call.RequestStream.WriteAsync(chunk, ct); }
            catch { _active = false; }
        }

        private static bool IsReadOnlyRefusal(global::Grpc.Core.RpcException ex) =>
            ex.StatusCode == global::Grpc.Core.StatusCode.ResourceExhausted
            && ex.Trailers.GetValue(StorageNodeErrors.ReasonTrailer) == StorageNodeErrors.ReadOnly;

        /// <summary>How long to wait for the status of a call whose send failed: a node that ended it has already answered.</summary>
        private static readonly TimeSpan StatusWait = TimeSpan.FromMilliseconds(250);

        public void Abort()
        {
            _active = false;
            _aborted = true;
            _call.Dispose();
        }

        public async Task CompleteAsync()
        {
            if (_aborted) return;
            try
            {
                if (_active)
                {
                    // The node may have ended the call already (a refusal at the header while our sends
                    // were still buffered); then completing the request stream throws, but the status
                    // below still says why.
                    try { await _call.RequestStream.CompleteAsync(); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _active = false; }
                }

                // A send failed: wait only briefly, so a black-holed node doesn't hold the whole write (#199).
                Response = _active ? await _call.ResponseAsync : await _call.ResponseAsync.WaitAsync(StatusWait);
            }
            catch (global::Grpc.Core.RpcException ex) when (IsReadOnlyRefusal(ex))
            {
                RefusedReadOnly = true;
            }
            catch { }
            finally
            {
                if (Response is null) _active = false;
            }
        }
    }
}

/// <summary>A blob stored at quorum: its content hash, size and the nodes holding it.</summary>
public sealed record BlobWriteResult(string Cid, long SizeBytes, IReadOnlyList<string> NodeIds);
