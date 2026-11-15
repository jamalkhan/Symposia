using Google.Protobuf;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Protocol;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Placement;
using Symposia.BlobStorage.Gateway.S3;

namespace Symposia.BlobStorage.Gateway.Quorum;

/// <summary>
/// Streams an incoming blob to its placement targets in parallel and enforces write quorum.
/// See Requirements/BlobStorage/gateway-architecture.md#write-path and
/// Requirements/BlobStorage/write-quorum-and-consistency.md.
/// </summary>
/// <remarks>
/// <para>
/// In <see cref="RedundancyMode.Network"/> the targets come from the blob's region assignment (#7 FR1,
/// #8 FR1) and the write is acknowledged the moment its quorum is met, counting only durable (fsynced)
/// node acks and requiring each target region's minimum (#7 FR2/FR3). The remaining copies keep
/// streaming in the background and are recorded as they confirm (#7 FR5). If quorum isn't met within the
/// quorum timeout after the body has been sent, every session is cancelled, nothing is committed, and
/// copies that did land are queued for garbage collection (#7 FR4).
/// </para>
/// <para>
/// In <see cref="RedundancyMode.Bootstrap"/> every writable node is a target and the write waits for all of
/// them (bounded by the same timeout) before acknowledging, as small clusters always have.
/// </para>
/// </remarks>
public sealed class QuorumWriter
{
    private const int ChunkSizeBytes = 64 * 1024;

    private readonly INodeRegistry _nodes;
    private readonly GatewayMetadataStore _metadata;
    private readonly IOptions<GatewayOptions> _options;
    private readonly Repair.RepairQueue _repairQueue;
    private readonly PlacementEngine _placement;
    private readonly Health.GatewayMetrics? _metrics;
    private readonly ILogger _logger;

    private readonly RegionAssignmentResolver _regions;
    private readonly Erasure.ErasureObjects? _erasure;

    public QuorumWriter(INodeRegistry nodes, GatewayMetadataStore metadata, IOptions<GatewayOptions> options,
        Repair.RepairQueue repairQueue, PlacementEngine? placement = null, Health.GatewayMetrics? metrics = null, ILogger<QuorumWriter>? logger = null,
        RegionAssignmentResolver? regions = null, Erasure.ErasureObjects? erasure = null)
    {
        _erasure = erasure;
        _regions = regions ?? new RegionAssignmentResolver(metadata);
        _nodes = nodes;
        _metadata = metadata;
        _options = options;
        _repairQueue = repairQueue;
        _placement = placement ?? new PlacementEngine(nodes, options);
        _metrics = metrics;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// Fans out the body stream to the placement targets, waits for quorum, commits metadata.
    /// Returns the committed ObjectRecord on success, null if quorum was not reached.
    /// </summary>
    public async Task<ObjectRecord?> WriteAsync(
        string tenantId, string bucket, string key, string contentType,
        Stream body, CancellationToken cancellationToken, long? expectedSize = null) =>
        (await WriteObjectAsync(tenantId, bucket, key, contentType, body, cancellationToken, expectedSize)).Record;

    /// <summary>As <see cref="WriteAsync"/>, also reporting whether the commit replaced an existing object (#204).</summary>
    public async Task<(ObjectRecord? Record, bool Replaced)> WriteObjectAsync(
        string tenantId, string bucket, string key, string contentType,
        Stream body, CancellationToken cancellationToken, long? expectedSize = null, IReadOnlyList<string>? objectRegions = null)
    {
        // A bucket configured for erasure coding stores shards instead of copies (#8 FR5.1).
        if (_erasure is not null && Domain.Erasure.ErasureScheme.TryParse(_metadata.BucketScheme(bucket), out var scheme))
        {
            return await _erasure.WriteAsync(tenantId, bucket, key, contentType, body, scheme, _regions.Resolve(bucket, key, objectRegions), cancellationToken);
        }

        var blob = await WriteBlobAsync(tenantId, bucket, key, body, cancellationToken, expectedSize, objectRegions);
        if (blob is null)
            return (null, false);

        var record = ToRecord(blob, bucket, key, contentType);
        var outcome = _metadata.CommitObject(record);
        record = record with { LastModified = outcome.CommittedAt };
        AfterCommit(blob, bucket, key);
        return (record, outcome.Replaced);
    }

    /// <summary>The object row for a stored blob: confirmed replicas only, with its copy target and region assignment.</summary>
    public static ObjectRecord ToRecord(BlobWriteResult blob, string bucket, string key, string contentType, string? etagOverride = null) =>
        new(bucket, key, blob.Cid, blob.SizeBytes, contentType, DateTimeOffset.UtcNow, blob.NodeIds, etagOverride)
        {
            TargetCopies = blob.Requirement.RepairTarget,
            Regions = blob.Requirement.Regions,
            PlacementGaps = blob.Gaps.Select(g => new PlacementGapRecord(g.Rule, g.Detail)).ToList(),
            MerkleRoot = blob.MerkleRoot,
        };

    /// <summary>
    /// After an object commit: records the copies that confirm past quorum on the object as they land (#7
    /// FR5), and queues repair for any the placement couldn't target. Shared with multipart Complete.
    /// </summary>
    public void AfterCommit(BlobWriteResult blob, string bucket, string key)
    {
        RepairIfShort(blob);
        _ = blob.Remainder.ContinueWith(late =>
        {
            foreach (var nodeId in late.Result)
            {
                _metadata.AddConfirmedReplica(bucket, key, blob.Cid, nodeId);
            }

            if (blob.NodeIds.Count + late.Result.Count < blob.Requirement.RepairTarget)
            {
                _repairQueue.Enqueue(new Repair.RepairTask(blob.Cid, null, Repair.RepairReason.UnderReplicated));
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>
    /// Quorum can be below the copy target (issue #135: a write succeeds on one node); queue the
    /// missing copies now rather than waiting for the next replication scan.
    /// </summary>
    public void RepairIfShort(BlobWriteResult blob)
    {
        if (blob.NodeIds.Count + blob.Pending < blob.Requirement.RepairTarget)
        {
            _repairQueue.Enqueue(new Repair.RepairTask(blob.Cid, null, Repair.RepairReason.UnderReplicated));
        }
    }

    /// <summary>
    /// Stores a blob on its placement targets and waits for quorum, without creating an object (multipart
    /// parts, #5). Null if quorum was not reached. Throws (after cancelling node writes) if reading the body
    /// fails, e.g. a failed integrity check, and a 503 rejection when the region assignment can't be placed.
    /// </summary>
    /// <remarks>
    /// Targets only nodes that take writes (#199); a node that refuses as read-only mid-write is
    /// dropped from this write without being marked unhealthy, and repair tops up the copies. The
    /// body is streamed, not buffered, so a refused copy can't be resent elsewhere: if too few
    /// nodes accept, quorum fails and the caller answers 503 for the client to retry.
    /// </remarks>
    public async Task<BlobWriteResult?> WriteBlobAsync(
        string tenantId, string bucket, string key, Stream body, CancellationToken cancellationToken, long? expectedSize = null,
        IReadOnlyList<string>? regions = null)
    {
        expectedSize ??= body.CanSeek ? body.Length - body.Position : null;
        var requirement = _placement.Requirement(_regions.Resolve(bucket, key, regions), expectedSize);
        PlacementPlan plan;
        try
        {
            plan = _placement.PlaceNew(requirement, expectedSize);
        }
        catch (PlacementException ex)
        {
            _metrics?.WriteQuorumFailed.Inc();
            if (!requirement.EnforcesRegions)
            {
                return null; // bootstrap: too few writable nodes is the familiar quorum miss
            }

            // A constraint that can't be met fails clearly; nothing is written elsewhere instead (#8 FR1.4).
            throw new S3RequestRejectedException(StatusCodes.Status503ServiceUnavailable, "InsufficientPlacementCapacity", ex.Message);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var header = new WriteBlobHeader { TenantId = tenantId, Bucket = bucket, Key = key, ExpectedSizeBytes = expectedSize ?? 0 };
        header.RegionTags.AddRange(requirement.Regions);
        var sessions = plan.Targets.Select(n => new NodeWriteSession(n)).ToList();

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
            // A bad or truncated request body, or the client went away: cancel the node streams so
            // nothing is committed (#1 FR-9); a node never renames an unfinished temp file into place.
            foreach (var session in sessions)
            {
                session.Abort();
            }

            throw;
        }

        // Every target now has the whole body; wait for durable acks, at most the quorum timeout.
        // A list, not a map: sessions that end synchronously share one completed Task instance.
        var completions = sessions.Select(s => new Pending(s.CompleteAsync(), s)).ToList();
        var confirmed = new List<NodeWriteSession>();
        using var timeout = new CancellationTokenSource(_options.Value.Redundancy.WriteQuorumTimeout(tenantId));
        var waitForAll = !requirement.EnforcesRegions;
        while (completions.Count > 0 && (waitForAll || !requirement.IsSatisfiedBy(confirmed.Select(c => c.Region))))
        {
            NodeWriteSession session;
            try
            {
                session = await NextAsync(completions, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break; // quorum timeout
            }

            if (session.RefusedReadOnly)
            {
                session.Node.MarkReadOnly();
            }

            if (session.Response is not null)
            {
                confirmed.Add(session);
            }
            else
            {
                _metrics?.NodeErrors.WithLabels(session.Node.Id, "write").Inc();
            }
        }

        // One content hash: a node reporting a different CID for the same bytes is faulty, not a copy.
        var cid = confirmed.FirstOrDefault()?.Response!.Cid;
        foreach (var odd in confirmed.Where(c => c.Response!.Cid != cid).ToList())
        {
            _logger.LogError("Node {Node} stored {Got} for a blob others stored as {Cid}; not counting it.", odd.Node.Id, odd.Response!.Cid, cid);
            confirmed.Remove(odd);
        }

        if (!requirement.IsSatisfiedBy(confirmed.Select(c => c.Region)))
        {
            // Quorum not reached in time (#7 FR4): cancel what's still streaming and discard what landed.
            // Discard goes through GC (unreferenced check + minimum age), never a direct delete: another
            // object may hold the same bytes (content-addressed dedup).
            foreach (var pending in completions)
            {
                pending.Session.Abort();
            }

            if (cid is not null)
            {
                _metadata.CollectIfUnreferenced(cid, confirmed.Select(c => c.Node.Id));
            }

            _metrics?.WriteQuorumFailed.Inc();
            _logger.LogWarning("Write of {Bucket}/{Key} missed quorum: {Confirmed} of {Needed} durable acks ({Targets} targets).",
                bucket, key, confirmed.Count, requirement.WriteQuorum, plan.Targets.Count);
            return null;
        }

        _metrics?.WriteQuorumReached.Inc();
        _metrics?.WriteFanoutDuration.Observe(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
        var remainder = completions.Count == 0
            ? Task.FromResult<IReadOnlyList<string>>([])
            : AwaitRemainderAsync(completions, cid!, TimeSpan.FromSeconds(Math.Max(1, _options.Value.Redundancy.RemainderTimeoutSeconds)));
        // The Merkle root is kept only when every confirming node computed the same one (#8 FR6.4).
        var roots = confirmed.Select(c => c.Response!.MerkleRoot).ToList();
        var merkleRoot = roots.All(r => r.Length == Domain.MerkleTree.HashSize && r.Equals(roots[0]))
            ? Convert.ToHexStringLower(roots[0].ToByteArray()) : null;
        return new BlobWriteResult(cid!, confirmed[0].Response!.SizeBytes, confirmed.Select(c => c.Node.Id).ToList())
        {
            MerkleRoot = merkleRoot,
            Requirement = requirement,
            Gaps = plan.Gaps,
            Pending = completions.Count,
            Remainder = remainder,
        };
    }

    private sealed record Pending(Task Completion, NodeWriteSession Session);

    /// <summary>Waits for the next session to finish and removes it from <paramref name="pending"/>.</summary>
    private static async Task<NodeWriteSession> NextAsync(List<Pending> pending, CancellationToken ct)
    {
        var done = await Task.WhenAny(pending.Select(p => p.Completion)).WaitAsync(ct);
        var index = pending.FindIndex(p => ReferenceEquals(p.Completion, done));
        var session = pending[index].Session;
        pending.RemoveAt(index);
        return session;
    }

    /// <summary>The copies still landing after quorum (#7 FR5): the node IDs that confirm the same CID in time.</summary>
    private async Task<IReadOnlyList<string>> AwaitRemainderAsync(List<Pending> pending, string cid, TimeSpan limit)
    {
        var late = new List<string>();
        using var timeout = new CancellationTokenSource(limit);
        while (pending.Count > 0)
        {
            NodeWriteSession session;
            try
            {
                session = await NextAsync(pending, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (session.RefusedReadOnly)
            {
                session.Node.MarkReadOnly();
            }

            if (session.Response?.Cid == cid)
            {
                late.Add(session.Node.Id);
            }
        }

        foreach (var slow in pending)
        {
            _logger.LogWarning("Copy of {Cid} on {Node} did not confirm within {Limit}; repair will place it.", cid, slow.Session.Node.Id, limit);
            slow.Session.Abort();
        }

        return late;
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
            Region = node.Region;
            _call = node.Client.WriteBlob();
        }

        public NodeConnection Node { get; }

        /// <summary>The node's region when it was chosen; what its ack counts toward (#7 FR3).</summary>
        public string Region { get; }

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

        /// <summary>Cancels the node's write: it discards its unfinished temp file, so nothing is stored.</summary>
        public void Abort()
        {
            _active = false;
            _aborted = true;
            _call.Dispose();
        }

        /// <summary>
        /// Ends the request stream and waits for the node's answer, which it sends only after the blob is
        /// fsynced and renamed into place: a response is a durable ack (#7 FR2).
        /// </summary>
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

/// <summary>A blob stored at quorum: its content hash, size and the nodes that durably confirmed it.</summary>
public sealed record BlobWriteResult(string Cid, long SizeBytes, IReadOnlyList<string> NodeIds)
{
    /// <summary>What the blob's region assignment required (copy target, quorum, regions).</summary>
    public QuorumRequirement Requirement { get; init; } = QuorumPolicy.Bootstrap(NodeIds.Count, 1, 1);

    /// <summary>Soft placement rules bent for this write (#8 FR2.3).</summary>
    public IReadOnlyList<PlacementGap> Gaps { get; init; } = [];

    /// <summary>Hex Merkle root agreed by every confirming node, or null.</summary>
    public string? MerkleRoot { get; init; }

    /// <summary>Copies still landing when quorum was reached.</summary>
    public int Pending { get; init; }

    /// <summary>Node IDs of the copies that confirmed after quorum (#7 FR5); completes when they all have, or time out.</summary>
    public Task<IReadOnlyList<string>> Remainder { get; init; } = Task.FromResult<IReadOnlyList<string>>([]);
}
