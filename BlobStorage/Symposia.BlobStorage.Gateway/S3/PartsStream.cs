using System.Security.Cryptography;
using Grpc.Core;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.S3;

/// <summary>
/// The concatenation of a multipart upload's parts, read in part-number order straight from the
/// storage nodes (issue #5). Each part is read from the first replica that starts streaming (healthy
/// nodes first, as GET does) and its bytes are checked against its CID before the next part starts,
/// so a corrupt replica can't end up inside the assembled object. Nothing is buffered beyond one chunk.
/// </summary>
public sealed class PartsStream(IReadOnlyList<MultipartPart> parts, INodeRegistry nodes, ILogger logger) : Stream
{
    private static readonly TimeSpan FirstChunkTimeout = TimeSpan.FromSeconds(10);

    private int _index = -1;
    private AsyncServerStreamingCall<ReadBlobChunk>? _call;
    private ReadOnlyMemory<byte> _pending;
    private IncrementalHash? _hash;

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        while (true)
        {
            if (!_pending.IsEmpty)
            {
                var count = Math.Min(destination.Length, _pending.Length);
                _pending[..count].CopyTo(destination);
                _pending = _pending[count..];
                return count;
            }

            if (_call is not null)
            {
                if (await _call.ResponseStream.MoveNext(ct))
                {
                    Take(_call.ResponseStream.Current);
                    continue;
                }

                FinishPart();
            }

            if (_index + 1 >= parts.Count)
            {
                return 0;
            }

            _index++;
            await OpenPartAsync(parts[_index], ct);
        }
    }

    private void Take(ReadBlobChunk chunk)
    {
        _hash!.AppendData(chunk.Data.Span);
        _pending = chunk.Data.Memory;
    }

    private void FinishPart()
    {
        var part = parts[_index];
        var actual = Convert.ToHexStringLower(_hash!.GetHashAndReset());
        _call!.Dispose();
        _call = null;
        if (!actual.Equals(part.Cid, StringComparison.OrdinalIgnoreCase))
        {
            throw new S3RequestRejectedException(500, "InternalError", $"Part {part.PartNumber} failed its integrity check while assembling the object.");
        }
    }

    private async Task OpenPartAsync(MultipartPart part, CancellationToken ct)
    {
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var replicas = nodes.All
            .Where(n => part.NodeIds.Contains(n.Id, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(n => n.IsHealthy)
            .ToList();

        foreach (var node in replicas)
        {
            var call = node.Client.ReadBlob(new ReadBlobRequest { Cid = part.Cid }, deadline: DateTime.UtcNow.AddHours(1), cancellationToken: ct);
            using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
            first.CancelAfter(FirstChunkTimeout);
            try
            {
                if (await call.ResponseStream.MoveNext(first.Token))
                {
                    Take(call.ResponseStream.Current);
                }

                _call = call;
                return;
            }
            catch (Exception ex) when (ex is RpcException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                call.Dispose();
                logger.LogWarning("Reading part {Part} (CID {Cid}) from {Url} failed ({Error}); trying another replica.",
                    part.PartNumber, part.Cid, node.Url, ex.Message);
                if (ex is not RpcException { StatusCode: StatusCode.NotFound })
                {
                    node.MarkUnhealthy();
                }
            }
        }

        throw new S3RequestRejectedException(503, "ServiceUnavailable", $"No replica of part {part.PartNumber} could be read.");
    }

    protected override void Dispose(bool disposing)
    {
        _call?.Dispose();
        _hash?.Dispose();
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
