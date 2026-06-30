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

    public QuorumWriter(INodeRegistry nodes, GatewayMetadataStore metadata, IOptions<GatewayOptions> options)
    {
        _nodes = nodes;
        _metadata = metadata;
        _quorumCount = options.Value.WriteQuorumCount;
    }

    /// <summary>
    /// Fans out the body stream to all healthy nodes, waits for quorum, commits metadata.
    /// Returns the committed ObjectRecord on success, null if quorum was not reached.
    /// </summary>
    public async Task<ObjectRecord?> WriteAsync(
        string tenantId, string bucket, string key, string contentType,
        Stream body, CancellationToken cancellationToken)
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
        while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var chunk = new WriteBlobChunk { Data = ByteString.CopyFrom(buffer, 0, read) };
            await Task.WhenAll(sessions.Select(s => s.SendChunkAsync(chunk, cancellationToken)));
        }

        // Complete all streams and collect results.
        await Task.WhenAll(sessions.Select(s => s.CompleteAsync()));

        var successes = sessions.Where(s => s.Response is not null).ToList();
        if (successes.Count < _quorumCount)
            return null; // Quorum not reached.

        var cid = successes[0].Response!.Cid;
        var sizeBytes = successes[0].Response!.SizeBytes;
        var nodeIds = successes.Select(s => s.Node.Url).ToList();

        var record = new ObjectRecord(
            bucket, key, cid, sizeBytes, contentType,
            DateTimeOffset.UtcNow, nodeIds);

        _metadata.PutObject(record);
        return record;
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
