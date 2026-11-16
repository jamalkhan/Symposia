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


    private readonly LocalBlobStore _blobStore;
    private readonly ManifestStore _manifestStore;
    private readonly NodeIdentity _nodeIdentity;
    private readonly StorageCapacityMonitor _capacity;
    private readonly Health.NodeMetrics _metrics;
    private readonly IOptions<StorageNodeOptions> _options;

    public StorageNodeGrpcService(
        LocalBlobStore blobStore,
        ManifestStore manifestStore,
        NodeIdentity nodeIdentity,
        StorageCapacityMonitor capacity,
        Health.NodeMetrics metrics,
        IOptions<StorageNodeOptions> options)
    {
        _options = options;
        _metrics = metrics;
        _blobStore = blobStore;
        _manifestStore = manifestStore;
        _nodeIdentity = nodeIdentity;
        _capacity = capacity;
    }

    private static RpcException ReadOnly() =>
        new(new Status(StatusCode.ResourceExhausted, "node is read-only: insufficient disk"),
            new Metadata { { StorageNodeErrors.ReasonTrailer, StorageNodeErrors.ReadOnly } });

    public override async Task<WriteBlobResponse> WriteBlob(
        IAsyncStreamReader<WriteBlobChunk> requestStream, ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken) ||
            requestStream.Current.PayloadCase != WriteBlobChunk.PayloadOneofCase.Header)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "First message must be a WriteBlobHeader."));
        }

        var header = requestStream.Current.Header;
        using var inFlight = _metrics.Begin();
        _metrics.WriteOp();
        // Repair copies arrive as ordinary writes from the gateway's RepairWorker (tenant "repair").
        var isRepair = header.TenantId == "repair";
        if (isRepair)
        {
            _metrics.ReplicationTasksActive.Inc();
        }

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
            var writeTask = _blobStore.WriteWithMerkleAsync(pipe.Reader.AsStream(), context.CancellationToken);

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
                    _metrics.Wrote(chunk.Data.Length);
                    if (isRepair)
                    {
                        _metrics.ReplicationBytes.Inc(chunk.Data.Length);
                    }

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

            (Cid Cid, long SizeBytes, byte[] MerkleRoot) written;
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

            var (cid, sizeBytes, merkleRoot) = written;
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

            return new WriteBlobResponse { Cid = cid.Value, SizeBytes = sizeBytes, MerkleRoot = ByteString.CopyFrom(merkleRoot) };
        }
        finally
        {
            _capacity.Release(reserved, stored);
            if (isRepair)
            {
                _metrics.ReplicationTasksActive.Dec();
            }
        }
    }

    // ENOSPC is errno 28 on Linux and macOS (HResult is the raw errno there); Windows uses
    // ERROR_DISK_FULL (112) and ERROR_HANDLE_DISK_FULL (39), which mean other things as errnos.
    public static bool IsDiskFull(IOException ex) =>
        OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) is 112 or 39 : ex.HResult == 28;

    public override async Task ReadBlob(
        ReadBlobRequest request, IServerStreamWriter<ReadBlobChunk> responseStream, ServerCallContext context)
    {
        var (cid, remaining, offset) = ResolveRange(request.Cid, request.Offset, request.Length);
        using var inFlight = _metrics.Begin();
        _metrics.ReadOp();

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

            _metrics.Read(read);
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
            Region = _options.Value.Region,
            PublicIp = _options.Value.PublicIp,
            Asn = _options.Value.Asn,
            FacilityId = _options.Value.FacilityId,
            UploadBytesPerSecond = _options.Value.UploadBytesPerSecond,
            IntegrityFailures = (long)_metrics.IntegrityFailures.Value,
        });
    }

    public override async Task<IntegrityChallengeResponse> IntegrityChallenge(
        IntegrityChallengeRequest request, ServerCallContext context)
    {
        if (request.MerkleProof)
        {
            return await MerkleChallengeAsync(request, context.CancellationToken);
        }

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

    /// <summary>
    /// Proof of storage (#8 FR6.4): the requested 64 KiB leaf and its sibling hashes. Answering needs the
    /// leaf's actual bytes, so a node that kept only hashes can't pass.
    /// </summary>
    private async Task<IntegrityChallengeResponse> MerkleChallengeAsync(IntegrityChallengeRequest request, CancellationToken ct)
    {
        if (!Cid.TryParse(request.Cid, out var cid) || !_blobStore.Exists(cid))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Blob '{request.Cid}' not found."));
        }

        var leaves = await _blobStore.ReadLeavesAsync(cid, ct);
        if (request.LeafIndex < 0 || request.LeafIndex >= leaves.Count)
        {
            throw new RpcException(new Status(StatusCode.OutOfRange, "Leaf index is outside the blob."));
        }

        var data = new byte[MerkleTree.LeafSize];
        int filled = 0;
        await using (var stream = _blobStore.OpenRead(cid, request.LeafIndex * MerkleTree.LeafSize))
        {
            int read;
            while (filled < data.Length && (read = await stream.ReadAsync(data.AsMemory(filled), ct)) > 0)
            {
                filled += read;
            }
        }

        var response = new IntegrityChallengeResponse
        {
            ProofSupported = true,
            LeafData = ByteString.CopyFrom(data, 0, filled),
            LeafCount = leaves.Count,
        };
        response.Siblings.AddRange(MerkleTree.Proof(leaves, request.LeafIndex).Select(ByteString.CopyFrom));
        return response;
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

    /// <summary>
    /// The manifest change feed (#12 Layer 2): a snapshot when the caller has no usable cursor, then every event
    /// after it, then live events as they commit, with a heartbeat carrying the node's digest when idle. Events
    /// are read from the durable outbox, so a gateway that reconnects with its last seq misses nothing that's
    /// still retained, and one further behind is told to reset and gets a fresh snapshot.
    /// </summary>
    public override async Task WatchManifest(
        WatchManifestRequest request, IServerStreamWriter<ManifestEvent> responseStream, ServerCallContext context)
    {
        const int Page = 500;
        var ct = context.CancellationToken;
        var cursor = request.AfterSeq;
        var oldest = _manifestStore.OldestSeq();
        var otherFeed = request.FeedId.Length > 0 && request.FeedId != _manifestStore.FeedId;
        if (otherFeed || cursor <= 0 || cursor < oldest - 1 || cursor > _manifestStore.HeadSeq())
        {
            // Read the head before the snapshot: anything committed while it streams is replayed after it.
            cursor = _manifestStore.HeadSeq();
            var reset = true;
            var after = "";
            while (true)
            {
                var rows = _manifestStore.SnapshotPage(after, Page);
                foreach (var row in rows)
                {
                    await responseStream.WriteAsync(ToEvent(row, reset), ct);
                    reset = false;
                }

                if (rows.Count < Page) break;
                after = rows[^1].Cid.Value;
            }

            if (reset)
            {
                // An empty manifest still has to tell the caller to forget what it knew.
                await responseStream.WriteAsync(new ManifestEvent
                {
                    Kind = ManifestEvent.Types.Kind.CaughtUp, Reset = true, HeadSeq = cursor, SchemaVersion = 1, FeedId = _manifestStore.FeedId,
                }, ct);
            }
        }

        var caughtUp = false;
        var heartbeat = TimeSpan.FromSeconds(Math.Max(1, _options.Value.ManifestHeartbeatSeconds));
        while (!ct.IsCancellationRequested)
        {
            // Arm before reading so a commit between the read and the wait isn't missed.
            var changed = _manifestStore.WaitForChangeAsync(ct);
            var events = _manifestStore.EventsAfter(cursor, Page);
            foreach (var change in events)
            {
                await responseStream.WriteAsync(ToEvent(change, reset: false), ct);
                cursor = change.Seq;
            }

            if (events.Count == Page)
            {
                continue;
            }

            if (!caughtUp)
            {
                caughtUp = true;
                await responseStream.WriteAsync(Digest(ManifestEvent.Types.Kind.CaughtUp, cursor), ct);
            }

            try
            {
                await changed.WaitAsync(heartbeat, ct);
            }
            catch (TimeoutException)
            {
                await responseStream.WriteAsync(Digest(ManifestEvent.Types.Kind.Heartbeat, cursor), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    // The digest's head and count are read together; a client compares counts only when that head is its cursor.
    private ManifestEvent Digest(ManifestEvent.Types.Kind kind, long cursor)
    {
        var (head, active) = _manifestStore.Digest();
        return new() { Kind = kind, HeadSeq = Math.Max(head, cursor), ActiveCount = active, SchemaVersion = 1, FeedId = _manifestStore.FeedId };
    }

    private ManifestEvent ToEvent(ManifestChange change, bool reset) => new()
    {
        FeedId = _manifestStore.FeedId,
        Kind = change.Removed ? ManifestEvent.Types.Kind.Removed : ManifestEvent.Types.Kind.Put,
        Seq = change.Seq,
        Cid = change.Cid.Value,
        SizeBytes = change.SizeBytes,
        Status = change.Status.ToString(),
        OccurredAtUnixMs = change.OccurredAt.ToUnixTimeMilliseconds(),
        Reset = reset,
        SchemaVersion = 1,
    };

    /// <summary>
    /// An inclusion proof for <c>cid</c> in the manifest root this node froze for an epoch (#12 Layer 3, FR3.2).
    /// Only epochs whose root reached the chain are answered, so a proof always refers to a root a verifier can
    /// fetch; not found otherwise, which a verifier treats as "not attested".
    /// </summary>
    public override Task<ManifestProofResponse> GetManifestProof(ManifestProofRequest request, ServerCallContext context)
    {
        if (!Cid.TryParse(request.Cid, out var cid))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.Cid}' is not a valid CID."));
        }

        var epoch = request.Epoch == 0 ? _manifestStore.LatestSubmittedEpoch() : _manifestStore.GetEpochRoot(request.Epoch);
        if (epoch is not { SubmittedAt: not null })
        {
            return Task.FromResult(new ManifestProofResponse { Found = false, Epoch = request.Epoch });
        }

        var proofIndex = _manifestStore.ProofIndex(epoch.Epoch);
        var leaves = proofIndex.Leaves;
        var index = proofIndex.IndexOf.TryGetValue(cid.Value, out var found) ? found : -1;
        var response = new ManifestProofResponse
        {
            Epoch = epoch.Epoch,
            Root = ByteString.CopyFrom(epoch.Root),
            LeafCount = leaves.Count,
        };
        if (index < 0)
        {
            return Task.FromResult(response);
        }

        response.Found = true;
        response.LeafIndex = index;
        response.SizeBytes = leaves[index].SizeBytes;
        response.TenantId = leaves[index].TenantId;
        response.Siblings.AddRange(MerkleTree.Proof(proofIndex.Hashes, index).Select(ByteString.CopyFrom));
        return Task.FromResult(response);
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
