using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain.Erasure;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Placement;
using Symposia.BlobStorage.Gateway.Quorum;
using Symposia.BlobStorage.Gateway.S3;
using Symposia.BlobStorage.Protocol;
using Shard = Symposia.BlobStorage.Gateway.Metadata.GatewayMetadataStore.Shard;

namespace Symposia.BlobStorage.Gateway.Erasure;

/// <summary>
/// Erasure-coded objects (#8 FR5): written as <c>k + m</c> shards, one per node, each an ordinary blob on its
/// node with its own CID. A shard is a placement unit, so the same placement engine and fault-domain rules
/// place shards as place copies (#8 FR5.2). Reads stream stripes from any <c>k</c> shards and decode when a
/// data shard is missing or fails mid-stream; repair rebuilds a lost shard from any <c>k</c> others.
/// The object's own CID is the SHA-256 of its bytes, checked on every full read as for replicated objects.
/// </summary>
public sealed class ErasureObjects(
    INodeRegistry nodes, GatewayMetadataStore store, PlacementEngine placement, IOptions<GatewayOptions> options,
    ILogger<ErasureObjects> logger)
{
    // For the nested shard readers, which can't see primary-constructor parameters.
    private GatewayMetadataStore Store => store;
    private INodeRegistry Nodes => nodes;
    private ILogger Logger => logger;

    /// <summary>
    /// The placement requirement for <paramref name="scheme"/>'s shards: global shards spread over as many
    /// regions as there are; region-assigned shards divided evenly over the target regions, each of which
    /// must confirm at least one before the write counts.
    /// </summary>
    public QuorumRequirement Requirement(ErasureScheme scheme, IReadOnlyList<string> regions)
    {
        var none = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var n = scheme.TotalShards;
        var network = options.Value.Redundancy.Mode == RedundancyMode.Network;
        if (regions.Count == 0 || !network)
        {
            return new QuorumRequirement(n, scheme.WriteQuorum, none, n, Math.Min(3, n), none) { EnforcesRegions = network };
        }

        var per = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            per[regions[i % regions.Count]] = per.GetValueOrDefault(regions[i % regions.Count]) + 1;
        }

        return new QuorumRequirement(n, scheme.WriteQuorum, per, 0, regions.Count,
            regions.ToDictionary(r => r, _ => 1, StringComparer.OrdinalIgnoreCase));
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Encodes and stores <paramref name="body"/>; null when the shard quorum wasn't reached in time.</summary>
    public async Task<(ObjectRecord? Record, bool Replaced)> WriteAsync(string tenantId, string bucket, string key, string contentType,
        Stream body, ErasureScheme scheme, IReadOnlyList<string> regions, CancellationToken ct, string? etagOverride = null)
    {
        var requirement = Requirement(scheme, regions);
        PlacementPlan plan;
        try
        {
            plan = placement.PlaceNew(requirement, null);
        }
        catch (PlacementException ex)
        {
            throw new S3RequestRejectedException(StatusCodes.Status503ServiceUnavailable, "InsufficientPlacementCapacity", ex.Message);
        }

        var codec = new ReedSolomon(scheme.DataShards, scheme.ParityShards);
        var sessions = plan.Targets.Take(scheme.TotalShards).Select((node, index) => new ShardSession(node, index)).ToList();
        var header = new WriteBlobHeader { TenantId = tenantId, Bucket = bucket, Key = key };
        header.RegionTags.AddRange(requirement.Regions);
        await Task.WhenAll(sessions.Select(s => s.SendAsync(new WriteBlobChunk { Header = header }, ct)));

        var units = Enumerable.Range(0, scheme.TotalShards).Select(_ => new byte[ErasureScheme.UnitSize]).ToArray();
        var stripe = new byte[scheme.StripeDataBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        try
        {
            while (true)
            {
                var filled = await FillAsync(body, stripe, ct);
                if (filled == 0 && size > 0)
                {
                    break;
                }

                hash.AppendData(stripe, 0, filled);
                size += filled;
                Array.Clear(stripe, filled, stripe.Length - filled);
                for (var d = 0; d < scheme.DataShards; d++)
                {
                    Buffer.BlockCopy(stripe, d * ErasureScheme.UnitSize, units[d], 0, ErasureScheme.UnitSize);
                }

                codec.Encode(units, ErasureScheme.UnitSize);
                await Task.WhenAll(sessions.Select(s => s.SendAsync(new WriteBlobChunk { Data = ByteString.CopyFrom(units[s.Index]) }, ct)));
                if (filled < stripe.Length)
                {
                    break;
                }
            }
        }
        catch
        {
            sessions.ForEach(s => s.Abort());
            throw;
        }

        var completions = sessions.Select(s => (Task: s.CompleteAsync(), Session: s)).ToList();
        var confirmed = new List<ShardSession>();
        using var timeout = new CancellationTokenSource(options.Value.Redundancy.WriteQuorumTimeout(tenantId));
        while (completions.Count > 0 && !requirement.IsSatisfiedBy(confirmed.Select(c => c.Region)))
        {
            try
            {
                var done = await Task.WhenAny(completions.Select(c => c.Task)).WaitAsync(timeout.Token);
                var index = completions.FindIndex(c => ReferenceEquals(c.Task, done));
                var session = completions[index].Session;
                completions.RemoveAt(index);
                if (session.Response is not null) confirmed.Add(session);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (!requirement.IsSatisfiedBy(confirmed.Select(c => c.Region)))
        {
            completions.ForEach(c => c.Session.Abort());
            foreach (var shard in confirmed)
            {
                store.CollectIfUnreferenced(shard.Response!.Cid, [shard.Node.Id]);
            }

            logger.LogWarning("Erasure-coded write of {Bucket}/{Key} missed its shard quorum: {Confirmed} of {Needed}.",
                bucket, key, confirmed.Count, scheme.WriteQuorum);
            return (null, false);
        }

        var cid = Convert.ToHexStringLower(hash.GetHashAndReset());
        var record = new ObjectRecord(bucket, key, cid, size, contentType, DateTimeOffset.UtcNow, [], etagOverride)
        {
            Scheme = scheme.ToString(),
            TargetCopies = scheme.TotalShards,
            Regions = requirement.Regions,
            PlacementGaps = plan.Gaps.Select(g => new PlacementGapRecord(g.Rule, g.Detail)).ToList(),
        };
        var outcome = store.CommitErasureObject(record, confirmed.Select(c => new Shard(c.Index, c.Response!.Cid, c.Node.Id)).ToList());

        // Shards past quorum join the map as they confirm (#7 FR5 for shards).
        _ = Task.Run(async () =>
        {
            foreach (var (task, session) in completions)
            {
                await task.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, options.Value.Redundancy.RemainderTimeoutSeconds))).ContinueWith(_ => { });
                if (session.Response is not null)
                {
                    store.PutShard(bucket, key, cid, new Shard(session.Index, session.Response.Cid, session.Node.Id));
                }
                else
                {
                    session.Abort();
                }
            }
        });

        return (record with { LastModified = outcome.CommittedAt }, outcome.Replaced);
    }

    private static async Task<int> FillAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        int read;
        while (filled < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(filled), ct)) > 0)
        {
            filled += read;
        }

        return filled;
    }

    private sealed class ShardSession(NodeConnection node, int index)
    {
        private readonly AsyncClientStreamingCall<WriteBlobChunk, WriteBlobResponse> _call = node.Client.WriteBlob();
        private bool _active = true;

        public NodeConnection Node { get; } = node;
        public int Index { get; } = index;
        public string Region { get; } = node.Region;
        public WriteBlobResponse? Response { get; private set; }

        public async Task SendAsync(WriteBlobChunk chunk, CancellationToken ct)
        {
            if (!_active) return;
            try { await _call.RequestStream.WriteAsync(chunk, ct); }
            catch { _active = false; }
        }

        public async Task CompleteAsync()
        {
            try
            {
                if (_active) await _call.RequestStream.CompleteAsync();
                Response = await _call.ResponseAsync.WaitAsync(_active ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(250));
            }
            catch { }
        }

        public void Abort()
        {
            _active = false;
            _call.Dispose();
        }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams the object's bytes <c>[offset, offset + length)</c> to <paramref name="output"/>, from any k
    /// shards; false (nothing written) when fewer than k shards can be opened.
    /// </summary>
    public async Task<bool> ReadAsync(ObjectRecord record, long offset, long length, Stream output, IncrementalHash? hash, CancellationToken ct,
        Action? beforeFirstByte = null)
    {
        if (!ErasureScheme.TryParse(record.Scheme, out var scheme))
        {
            return false;
        }

        if (length == 0)
        {
            beforeFirstByte?.Invoke();
            return true;
        }

        var codec = new ReedSolomon(scheme.DataShards, scheme.ParityShards);
        var firstStripe = offset / scheme.StripeDataBytes;
        var lastStripe = (offset + length - 1) / scheme.StripeDataBytes;
        using var readers = new ShardReaders(this, record, scheme, firstStripe, lastStripe - firstStripe + 1);
        if (!await readers.OpenAsync(ct))
        {
            return false;
        }

        beforeFirstByte?.Invoke();
        var units = Enumerable.Range(0, scheme.TotalShards).Select(_ => new byte[ErasureScheme.UnitSize]).ToArray();
        var present = new bool[scheme.TotalShards];
        for (var s = firstStripe; s <= lastStripe; s++)
        {
            await readers.ReadStripeAsync(units, present, ct);
            if (Enumerable.Range(0, scheme.DataShards).Any(d => !present[d]))
            {
                codec.Reconstruct(units, present, ErasureScheme.UnitSize);
            }

            var stripeStart = s * scheme.StripeDataBytes;
            var from = Math.Max(offset, stripeStart) - stripeStart;
            var to = Math.Min(offset + length, stripeStart + scheme.StripeDataBytes) - stripeStart;
            for (var d = 0; d < scheme.DataShards && from < to; d++)
            {
                var unitStart = (long)d * ErasureScheme.UnitSize;
                var a = Math.Max(from, unitStart);
                var b = Math.Min(to, unitStart + ErasureScheme.UnitSize);
                if (a >= b) continue;
                var slice = units[d].AsMemory((int)(a - unitStart), (int)(b - a));
                hash?.AppendData(slice.Span);
                await output.WriteAsync(slice, ct);
            }
        }

        return true;
    }

    /// <summary>
    /// One open stream per used shard, positioned at the first stripe. A shard that fails is replaced by an
    /// unused one opened at the current stripe, so a node dying mid-read costs a decode, not the read.
    /// </summary>
    private sealed class ShardReaders(ErasureObjects owner, ObjectRecord record, ErasureScheme scheme, long firstStripe, long stripes) : IDisposable
    {
        private readonly Dictionary<int, (NodeConnection Node, AsyncServerStreamingCall<ReadBlobChunk> Call, Queue<ReadOnlyMemory<byte>> Buffered)> _open = [];
        private readonly HashSet<int> _failed = [];
        private IReadOnlyList<Shard> _shards = [];
        private long _stripe = firstStripe;

        public async Task<bool> OpenAsync(CancellationToken ct)
        {
            _shards = owner.Store.Shards(record.Bucket, record.Key);
            while (_open.Count < scheme.DataShards)
            {
                if (await OpenNextAsync(ct) is null)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Never read this shard (the one being rebuilt).</summary>
        public void Exclude(int index) => _failed.Add(index);

        /// <summary>The nodes currently being read from (their uploads count against the bandwidth budget).</summary>
        public IEnumerable<NodeConnection> Nodes => _open.Values.Select(v => v.Node);

        /// <summary>Opens the best unused shard at the current stripe; its index, or null when none is left.</summary>
        private async Task<int?> OpenNextAsync(CancellationToken ct)
        {
            // Data shards first (no decode needed), healthiest node first.
            var candidates = _shards.Where(s => !_open.ContainsKey(s.Index) && !_failed.Contains(s.Index))
                .Select(s => (Shard: s, Node: owner.Nodes.All.FirstOrDefault(n => n.Id.Equals(s.NodeId, StringComparison.OrdinalIgnoreCase))))
                .Where(c => c.Node is not null && c.Node.PenaltyStage < 3)
                .OrderBy(c => c.Shard.Index >= scheme.DataShards).ThenByDescending(c => c.Node!.IsHealthy).ToList();
            foreach (var (shard, node) in candidates)
            {
                var request = new ReadBlobRequest
                {
                    Cid = shard.Cid,
                    Offset = _stripe * ErasureScheme.UnitSize,
                    Length = (firstStripe + stripes - _stripe) * ErasureScheme.UnitSize,
                };
                var opened = await ReplicaReader.OpenAsync([node!], request, owner.Logger, ct);
                if (opened is null)
                {
                    _failed.Add(shard.Index);
                    continue;
                }

                var buffered = new Queue<ReadOnlyMemory<byte>>();
                if (opened.HasFirst) buffered.Enqueue(opened.Call.ResponseStream.Current.Data.Memory);
                _open[shard.Index] = (node!, opened.Call, buffered);
                return shard.Index;
            }

            return null;
        }

        public async Task ReadStripeAsync(byte[][] units, bool[] present, CancellationToken ct)
        {
            Array.Clear(present);
            foreach (var index in _open.Keys.ToList())
            {
                if (await TryReadUnitAsync(index, units[index], ct))
                {
                    present[index] = true;
                    continue;
                }

                // The shard failed mid-stream: drop it and bring in another at this stripe.
                var failed = _open[index];
                failed.Call.Dispose();
                _open.Remove(index);
                _failed.Add(index);
                owner.Logger.LogWarning("Shard {Index} of {Bucket}/{Key} failed on {Node} at stripe {Stripe}; decoding from another.",
                    index, record.Bucket, record.Key, failed.Node.Id, _stripe);
                while (await OpenNextAsync(ct) is int replacement)
                {
                    if (await TryReadUnitAsync(replacement, units[replacement], ct))
                    {
                        present[replacement] = true;
                        break;
                    }

                    _open[replacement].Call.Dispose();
                    _open.Remove(replacement);
                    _failed.Add(replacement);
                }
            }

            if (present.Count(p => p) < scheme.DataShards)
            {
                throw new IOException($"Fewer than {scheme.DataShards} shards of {record.Bucket}/{record.Key} are readable.");
            }

            _stripe++;
        }

        private async Task<bool> TryReadUnitAsync(int index, byte[] unit, CancellationToken ct)
        {
            var (_, call, buffered) = _open[index];
            var filled = 0;
            try
            {
                while (filled < unit.Length)
                {
                    if (buffered.Count == 0)
                    {
                        if (!await call.ResponseStream.MoveNext(ct)) return false;
                        buffered.Enqueue(call.ResponseStream.Current.Data.Memory);
                    }

                    var chunk = buffered.Dequeue();
                    var take = Math.Min(chunk.Length, unit.Length - filled);
                    chunk[..take].CopyTo(unit.AsMemory(filled));
                    filled += take;
                    if (take < chunk.Length) buffered.Enqueue(chunk[take..]); // rest belongs to the next unit (keeps order: queue was empty)
                }

                return true;
            }
            catch (RpcException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        public void Dispose()
        {
            foreach (var (_, call, _) in _open.Values) call.Dispose();
        }
    }

    // ── Repair ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds shard <paramref name="missing"/> from any k others and stores it on a node chosen under the
    /// same placement rules (#8 FR5.3); returns the new shard, or null when it can't be rebuilt yet.
    /// </summary>
    public async Task<Shard?> RebuildShardAsync(ObjectRecord record, int missing, Repair.BandwidthBudget? budget, CancellationToken ct)
    {
        if (!ErasureScheme.TryParse(record.Scheme, out var scheme))
        {
            return null;
        }

        var shards = store.Shards(record.Bucket, record.Key);
        var holders = shards.Where(s => s.Index != missing)
            .Select(s => nodes.All.FirstOrDefault(n => n.Id.Equals(s.NodeId, StringComparison.OrdinalIgnoreCase))).OfType<NodeConnection>().ToList();
        var target = placement.PlaceAdditional(Requirement(scheme, record.Regions), holders, 1, null,
            exclude: shards.Select(s => s.NodeId).ToList()).Targets.FirstOrDefault();
        if (target is null)
        {
            logger.LogWarning("No eligible node to rebuild shard {Index} of {Bucket}/{Key} on.", missing, record.Bucket, record.Key);
            return null;
        }

        var codec = new ReedSolomon(scheme.DataShards, scheme.ParityShards);
        var stripes = scheme.Stripes(record.SizeBytes);
        using var readers = new ShardReaders(this, record, scheme, 0, stripes);
        readers.Exclude(missing);
        if (!await readers.OpenAsync(ct))
        {
            return null;
        }

        using var write = target.Client.WriteBlob(cancellationToken: ct);
        await write.RequestStream.WriteAsync(new WriteBlobChunk { Header = new WriteBlobHeader { TenantId = "repair", Bucket = record.Bucket, Key = record.Key } }, ct);
        var units = Enumerable.Range(0, scheme.TotalShards).Select(_ => new byte[ErasureScheme.UnitSize]).ToArray();
        var present = new bool[scheme.TotalShards];
        for (long s = 0; s < stripes; s++)
        {
            await readers.ReadStripeAsync(units, present, ct);
            present[missing] = false;
            codec.Reconstruct(units, present, ErasureScheme.UnitSize);
            if (budget is not null)
            {
                await budget.AcquireAsync([target, .. readers.Nodes], ErasureScheme.UnitSize, ct);
            }

            await write.RequestStream.WriteAsync(new WriteBlobChunk { Data = ByteString.CopyFrom(units[missing]) }, ct);
        }

        await write.RequestStream.CompleteAsync();
        var response = await write.ResponseAsync;
        var shard = new Shard(missing, response.Cid, target.Id);
        return store.PutShard(record.Bucket, record.Key, record.Cid, shard) ? shard : null;
    }
}
