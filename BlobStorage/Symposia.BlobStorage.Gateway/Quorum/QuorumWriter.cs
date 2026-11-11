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

    public QuorumWriter(INodeRegistry nodes, GatewayMetadataStore metadata, IOptions<GatewayOptions> options,
        Repair.RepairQueue repairQueue)
    {
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
        Stream body, CancellationToken cancellationToken) =>
        (await WriteObjectAsync(tenantId, bucket, key, contentType, body, cancellationToken)).Record;

    /// <summary>As <see cref="WriteAsync"/>, also reporting whether the commit replaced an existing object (#204).</summary>
    public async Task<(ObjectRecord? Record, bool Replaced)> WriteObjectAsync(
        string tenantId, string bucket, string key, string contentType,
        Stream body, CancellationToken cancellationToken)
    {
        var blob = await WriteBlobAsync(tenantId, bucket, key, body, cancellationToken);
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
    public async Task<BlobWriteResult?> WriteBlobAsync(
        string tenantId, string bucket, string key, Stream body, CancellationToken cancellationToken)
    {
        var targetNodes = _nodes.Healthy;
        if (targetNodes.Count == 0)
            return null;

        var header = new WriteBlobHeader { TenantId = tenantId, Bucket = bucket, Key = key };
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

        var successes = sessions.Where(s => s.Response is not null).ToList();
        if (successes.Count < _quorumCount)
            return null; // Quorum not reached.

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
        private readonly global::Grpc.Core.AsyncClientStreamingCall<WriteBlobChunk, WriteBlobResponse> _call;
        private bool _active = true;

        public NodeWriteSession(NodeConnection node)
        {
            Node = node;
            _call = node.Client.WriteBlob();
        }

        public NodeConnection Node { get; }
        public WriteBlobResponse? Response { get; private set; }

        public async Task SendHeaderAsync(WriteBlobHeader header, CancellationToken ct)
        {
            if (!_active) return;
            try { await _call.RequestStream.WriteAsync(new WriteBlobChunk { Header = header }, ct); }
            catch { _active = false; }
        }

        public async Task SendChunkAsync(WriteBlobChunk chunk, CancellationToken ct)
        {
            if (!_active) return;
            try { await _call.RequestStream.WriteAsync(chunk, ct); }
            catch { _active = false; }
        }

        public void Abort()
        {
            _active = false;
            _call.Dispose();
        }

        public async Task CompleteAsync()
        {
            if (!_active) return;
            try
            {
                await _call.RequestStream.CompleteAsync();
                Response = await _call.ResponseAsync;
            }
            catch { _active = false; }
        }
    }
}

/// <summary>A blob stored at quorum: its content hash, size and the nodes holding it.</summary>
public sealed record BlobWriteResult(string Cid, long SizeBytes, IReadOnlyList<string> NodeIds);
