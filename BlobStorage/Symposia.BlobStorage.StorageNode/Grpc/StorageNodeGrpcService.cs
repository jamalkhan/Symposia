using System.IO.Pipelines;
using System.Security.Cryptography;
using global::Grpc.Core;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.Protocol;
using Symposia.Network.Node;
using Symposia.BlobStorage.StorageNode.Storage;

namespace Symposia.BlobStorage.StorageNode.Grpc;

/// <summary>
/// Server side of the private gateway-to-node protocol.
/// See Requirements/BlobStorage/gateway-architecture.md#gateway-node-protocol.
/// </summary>
public sealed class StorageNodeGrpcService : Protocol.StorageNode.StorageNodeBase
{
    private const int ChunkSizeBytes = 64 * 1024;

    /// <summary>Trailer on a write refused for disk space (#199), so gateways can tell it from other ResourceExhausted errors.</summary>
    public const string ReasonTrailer = "symposia-reason";
    public const string ReadOnlyReason = "read-only";

    private readonly LocalBlobStore _blobStore;
    private readonly ManifestStore _manifestStore;
    private readonly NodeIdentity _nodeIdentity;
    private readonly StorageCapacityMonitor _capacity;

    public StorageNodeGrpcService(
        LocalBlobStore blobStore,
        ManifestStore manifestStore,
        NodeIdentity nodeIdentity,
        StorageCapacityMonitor capacity)
    {
        _blobStore = blobStore;
        _manifestStore = manifestStore;
        _nodeIdentity = nodeIdentity;
        _capacity = capacity;
    }

    private static RpcException ReadOnly() =>
        new(new Status(StatusCode.ResourceExhausted, "node is read-only: insufficient disk"),
            new Metadata { { ReasonTrailer, ReadOnlyReason } });

    public override async Task<WriteBlobResponse> WriteBlob(
        IAsyncStreamReader<WriteBlobChunk> requestStream, ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken) ||
            requestStream.Current.PayloadCase != WriteBlobChunk.PayloadOneofCase.Header)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "First message must be a WriteBlobHeader."));
        }

        var header = requestStream.Current.Header;

        // Admission (#199): refuse up front while read-only or when the stated size can't fit; a body
        // of unknown size reserves as it streams.
        var reserved = Math.Max(0, header.ExpectedSizeBytes);
        if (!_capacity.TryReserve(reserved))
        {
            throw ReadOnly();
        }

        long received = 0;
        long stored = 0;
        try
        {
            // A Pipe lets us stream gRPC chunks straight to disk without buffering the full blob in memory.
            var pipe = new Pipe();
            var writeTask = _blobStore.WriteAsync(pipe.Reader.AsStream(), context.CancellationToken);

            Exception? pumpError = null;
            try
            {
                while (await requestStream.MoveNext(context.CancellationToken))
                {
                    var chunk = requestStream.Current;
                    if (chunk.PayloadCase != WriteBlobChunk.PayloadOneofCase.Data)
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "Only the first message may be a header."));
                    }

                    received += chunk.Data.Length;
                    if (received > reserved)
                    {
                        var more = received - reserved;
                        if (!_capacity.TryReserve(more))
                        {
                            throw ReadOnly();
                        }

                        reserved += more;
                    }

                    await pipe.Writer.WriteAsync(chunk.Data.Memory, context.CancellationToken);
                }
            }
            catch (Exception ex)
            {
                pumpError = ex;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(pumpError);
            }

            (Cid Cid, long SizeBytes) written;
            try
            {
                written = await writeTask;
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                // Something else filled the volume: re-measure now so the node goes read-only at once.
                _capacity.Evaluate();
                throw ReadOnly();
            }

            if (pumpError is RpcException { StatusCode: StatusCode.ResourceExhausted } refused)
            {
                throw refused;
            }

            var (cid, sizeBytes) = written;
            stored = sizeBytes;

            _manifestStore.Upsert(new BlobRecord(
                cid,
                sizeBytes,
                header.TenantId,
                header.Bucket,
                header.Key,
                header.RegionTags.ToArray(),
                DateTimeOffset.UtcNow,
                ChecksumVerifiedAt: null,
                BlobStatus.Active));

            return new WriteBlobResponse { Cid = cid.Value, SizeBytes = sizeBytes };
        }
        finally
        {
            _capacity.Release(reserved, stored);
        }
    }

    // ENOSPC (28 on Linux and macOS) / ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL on Windows.
    private static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is 28 or 112 or 39;

    public override async Task ReadBlob(
        ReadBlobRequest request, IServerStreamWriter<ReadBlobChunk> responseStream, ServerCallContext context)
    {
        var (cid, remaining, offset) = ResolveRange(request.Cid, request.Offset, request.Length);

        await using var stream = _blobStore.OpenRead(cid, offset);
        var buffer = new byte[ChunkSizeBytes];

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(ChunkSizeBytes, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), context.CancellationToken);
            if (read == 0)
            {
                break;
            }

            await responseStream.WriteAsync(
                new ReadBlobChunk { Data = ByteString.CopyFrom(buffer, 0, read) }, context.CancellationToken);
            remaining -= read;
        }
    }

    public override Task<DeleteBlobResponse> DeleteBlob(DeleteBlobRequest request, ServerCallContext context)
    {
        if (!Cid.TryParse(request.Cid, out var cid))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.Cid}' is not a valid CID."));
        }

        var deletedFromDisk = _blobStore.Delete(cid);
        _manifestStore.Delete(cid);

        return Task.FromResult(new DeleteBlobResponse { Deleted = deletedFromDisk });
    }

    public override Task<ProbeResponse> Probe(ProbeRequest request, ServerCallContext context)
    {
        // One capacity computation for Probe, heartbeats and health (#199 FR10).
        var capacity = _capacity.Current;
        return Task.FromResult(new ProbeResponse
        {
            NodeId = _nodeIdentity.NodeId,
            UsedStorageBytes = capacity.Used,
            AvailableStorageBytes = capacity.Available,
            BlobCount = _manifestStore.CountBlobs(),
            // Critical: below the reserve, so the network repairs this node's data elsewhere (FR11).
            Healthy = capacity.Mode != StorageMode.Critical,
            ReadOnly = capacity.ReadOnly,
            VolumeFreeBytes = capacity.Free,
            ReserveBytes = capacity.Reserve,
        });
    }

    public override async Task<IntegrityChallengeResponse> IntegrityChallenge(
        IntegrityChallengeRequest request, ServerCallContext context)
    {
        var (cid, remaining, offset) = ResolveRange(request.Cid, request.Offset, request.Length);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = _blobStore.OpenRead(cid, offset);
        var buffer = new byte[ChunkSizeBytes];

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(ChunkSizeBytes, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), context.CancellationToken);
            if (read == 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return new IntegrityChallengeResponse { Sha256Hex = Convert.ToHexStringLower(hasher.GetHashAndReset()) };
    }

    public override async Task ListBlobs(
        ListBlobsRequest request, IServerStreamWriter<ListBlobsChunk> responseStream, ServerCallContext context)
    {
        const int PageSize = 100;
        var afterCid = request.AfterCid;

        while (true)
        {
            var page = _manifestStore.ListCidsPaged(afterCid, PageSize);
            if (page.Count == 0) break;

            var chunk = new ListBlobsChunk();
            chunk.Cids.AddRange(page);
            await responseStream.WriteAsync(chunk, context.CancellationToken);

            if (page.Count < PageSize) break;
            afterCid = page[^1];
        }
    }

    private (Cid Cid, long Remaining, long Offset) ResolveRange(string cidValue, long offset, long length)
    {
        if (!Cid.TryParse(cidValue, out var cid) || !_blobStore.Exists(cid))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Blob '{cidValue}' not found."));
        }

        var totalLength = _blobStore.GetLength(cid);
        var remaining = length > 0 ? length : totalLength - offset;

        if (offset < 0 || offset > totalLength || remaining < 0)
        {
            throw new RpcException(new Status(StatusCode.OutOfRange, "Requested range is outside the blob's bounds."));
        }

        return (cid, remaining, offset);
    }
}
