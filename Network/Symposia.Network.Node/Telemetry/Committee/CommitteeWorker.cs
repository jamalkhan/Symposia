using Symposia.Blockchain.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Hex.HexConvertors.Extensions;

namespace Symposia.Network.Node.Telemetry.Committee;

public sealed class TelemetryCommitteeOptions
{
    /// <summary>Run committee duties when this node is a registry committee member.</summary>
    public bool Enabled { get; set; } = true;

    public int PollSeconds { get; set; } = 15;

    /// <summary>Wait after a window closes before proposing, so late gossip arrives (#158 FR2.1).</summary>
    public int SettleDelaySeconds { get; set; } = 600;

    /// <summary>How long a proposer gets before the next member in order proposes (#158 FR2.5).</summary>
    public int ProposalTimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// One committee member's duties (issue #158 FR2/FR4), run as idempotent passes driven by block
/// time and chain state, so a restart just picks up where things stand:
/// <list type="bullet">
/// <item><b>Propose</b>: for each closed, uncommitted window, the member at rotation
/// <c>(epoch × windows + k + r) mod M</c> builds the batch from its admitted samples, stores
/// it, and gossips its own endorsement. <c>r</c> advances every proposal timeout.</item>
/// <item><b>Endorse</b>: other members fetch the proposed batch, check the hash, signatures,
/// window and root, and endorse only if it holds every sample they hold for that window.
/// Otherwise they push their extra samples to the committee, so the next proposer has the union.</item>
/// <item><b>Submit</b>: whoever holds threshold endorsements for one batch submits it through
/// the relay.</item>
/// <item><b>Records</b>: after the telemetry deadline, every member rebuilds the epoch only from
/// committed batches (committee and self-checkpoints), aggregates per node, chunks by the
/// on-chain <c>recordsPerTx</c>, signs, and submits once threshold signatures are held.</item>
/// </list>
/// </summary>
public sealed class CommitteeWorker(
    ITelemetryChain chain,
    ITelemetryRelay relay,
    ITelemetryBlobStore blobs,
    TelemetryGossipNode gossip,
    ITelemetrySigner signer,
    TelemetryEip712 eip712,
    IHttpClientFactory http,
    IOptions<TelemetryCommitteeOptions> options,
    ulong chainId,
    string registry,
    ILogger<CommitteeWorker> logger)
{
    private readonly TelemetryCommitteeOptions _options = options.Value;
    private readonly HashSet<(ulong, ushort, int)> _proposed = [];
    private readonly HashSet<(ulong, ushort, string)> _validated = [];
    private readonly HashSet<(ulong, ushort, string)> _refused = [];

    public CommitteeMetrics Metrics { get; } = new();

    /// <summary>One pass over the previous and current epochs. Does nothing unless this node is on the committee.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var (members, threshold) = await chain.CommitteeAsync(ct);
        var sorted = members.OrderBy(m => m.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        var self = sorted.FindIndex(m => string.Equals(m, signer.Address, StringComparison.OrdinalIgnoreCase));
        if (self < 0)
        {
            return;
        }

        var schedule = await chain.ScheduleAsync(ct);
        var now = await chain.NowAsync(ct);
        var current = schedule.EpochAt(now);
        foreach (var epoch in current == 0 ? [0UL] : new[] { current - 1, current })
        {
            var deadline = await chain.TelemetryDeadlineAsync(epoch, ct);
            if (now <= deadline)
            {
                await CheckpointWindowsAsync(schedule, epoch, now, sorted, threshold, ct);
            }
            else
            {
                await RecordsAsync(schedule, epoch, sorted, threshold, ct);
            }
        }

        gossip.Committee.PruneBefore(current == 0 ? 0 : current - 1);
    }

    private async Task CheckpointWindowsAsync(TelemetrySchedule schedule, ulong epoch, long now, IReadOnlyList<string> members,
        int threshold, CancellationToken ct)
    {
        for (ushort k = 0; k < schedule.CheckpointsPerEpoch; k++)
        {
            var settled = WindowEnd(schedule, epoch, k) + _options.SettleDelaySeconds;
            if (now < settled || await chain.CheckpointAsync(epoch, k, ct) is not null)
            {
                continue;
            }

            var rotation = (int)((now - settled) / Math.Max(1, _options.ProposalTimeoutSeconds));
            var proposer = members[(int)((epoch * (ulong)schedule.CheckpointsPerEpoch + k + (ulong)rotation) % (ulong)members.Count)];
            if (string.Equals(proposer, signer.Address, StringComparison.OrdinalIgnoreCase) && _proposed.Add((epoch, k, rotation)))
            {
                await ProposeAsync(schedule, epoch, k, ct);
            }

            await EndorseAsync(schedule, epoch, k, members, ct);
            await SubmitCheckpointAsync(epoch, k, members, threshold, ct);
        }
    }

    private async Task ProposeAsync(TelemetrySchedule schedule, ulong epoch, ushort k, CancellationToken ct)
    {
        var (heartbeats, attestations) = Held(schedule, epoch, k);
        if (heartbeats.Count + attestations.Count == 0)
        {
            return; // empty windows aren't checkpointed
        }

        var bytes = TelemetryBatchCodec.Encode(epoch, k, heartbeats, attestations);
        var address = TelemetryBatchCodec.Address(bytes);
        await blobs.PutAsync(address, bytes, ct);
        var batch = TelemetryBatchCodec.Decode(bytes, schedule, chainId, registry);
        Endorse(epoch, k, batch.MerkleRoot, address, batch.SampleCount);
        Metrics.Proposals.Increment();
        logger.LogInformation("Proposed checkpoint {Epoch}/{Window} with {Count} samples.", epoch, k, batch.SampleCount);
    }

    private async Task EndorseAsync(TelemetrySchedule schedule, ulong epoch, ushort k, IReadOnlyList<string> members, CancellationToken ct)
    {
        foreach (var proposal in gossip.Committee.Endorsements(epoch, k)
                     .Where(e => members.Contains(e.Member, StringComparer.OrdinalIgnoreCase))
                     .DistinctBy(e => e.Content))
        {
            var key = (epoch, k, proposal.BlobAddress.ToLowerInvariant());
            if (_validated.Contains(key) || _refused.Contains(key))
            {
                continue;
            }

            var bytes = await blobs.GetAsync(proposal.BlobAddress.HexToByteArray(), ct);
            if (bytes is null)
            {
                continue; // not fetchable yet; retry next pass
            }

            DecodedBatch batch;
            try
            {
                batch = TelemetryBatchCodec.DecodeCommitted(bytes, proposal.BlobAddress.HexToByteArray(), proposal.MerkleRoot.HexToByteArray(),
                    schedule, chainId, registry);
            }
            catch (InvalidDataException ex)
            {
                _refused.Add(key);
                Metrics.Refusals.Increment();
                logger.LogWarning("Refusing checkpoint {Epoch}/{Window} from {Member}: {Reason}", epoch, k, proposal.Member, ex.Message);
                continue;
            }

            var (heldHeartbeats, heldAttestations) = Held(schedule, epoch, k);
            var missingHeartbeats = heldHeartbeats.Except(batch.Heartbeats).ToList();
            var missingAttestations = heldAttestations.Except(batch.Attestations).ToList();
            if (missingHeartbeats.Count + missingAttestations.Count > 0 || batch.SampleCount != proposal.SampleCount)
            {
                // Censorship or a gossip gap: refuse, and make sure the rest of the committee has what we hold.
                _refused.Add(key);
                Metrics.Refusals.Increment();
                logger.LogWarning("Refusing checkpoint {Epoch}/{Window} from {Member}: it omits {Missing} samples this member holds.",
                    epoch, k, proposal.Member, missingHeartbeats.Count + missingAttestations.Count);
                await gossip.PushToAsync(http.CreateClient(nameof(TelemetryGossipNode)), members,
                    new TelemetryEnvelope(missingHeartbeats, missingAttestations), ct);
                continue;
            }

            _validated.Add(key);
            await blobs.PutAsync(proposal.BlobAddress.HexToByteArray(), bytes, ct); // keep a local copy for records and challenges
            Endorse(epoch, k, batch.MerkleRoot, proposal.BlobAddress.HexToByteArray(), batch.SampleCount);
        }
    }

    private void Endorse(ulong epoch, ushort k, byte[] root, byte[] blob, uint count)
    {
        var digest = eip712.CheckpointDigest(epoch, k, root, blob, count);
        gossip.Publish(new CheckpointEndorsement(epoch, k, root.ToHex(true), blob.ToHex(true), count, signer.Address,
            signer.Sign(digest).ToHex(true)));
        Metrics.Endorsements.Increment();
    }

    private async Task SubmitCheckpointAsync(ulong epoch, ushort k, IReadOnlyList<string> members, int threshold, CancellationToken ct)
    {
        var mine = gossip.Committee.Endorsements(epoch, k)
            .Where(e => string.Equals(e.Member, signer.Address, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Content)
            .ToHashSet();
        foreach (var group in gossip.Committee.Endorsements(epoch, k)
                     .Where(e => members.Contains(e.Member, StringComparer.OrdinalIgnoreCase) && mine.Contains(e.Content))
                     .GroupBy(e => e.Content))
        {
            var signatures = Sorted(group.DistinctBy(e => e.Member.ToLowerInvariant()).Select(e => (e.Member, e.Signature)), threshold);
            if (signatures is null)
            {
                continue;
            }

            var first = group.First();
            await relay.CommitCheckpointAsync(epoch, k, first.MerkleRoot.HexToByteArray(), first.BlobAddress.HexToByteArray(), first.SampleCount,
                signatures, ct);
            Metrics.Submissions.Increment();
            logger.LogInformation("Submitted checkpoint {Epoch}/{Window}.", epoch, k);
            return;
        }
    }

    private async Task RecordsAsync(TelemetrySchedule schedule, ulong epoch, IReadOnlyList<string> members, int threshold, CancellationToken ct)
    {
        if (await chain.RecordsFinalizedAsync(epoch, ct))
        {
            return;
        }

        var records = await BuildRecordsAsync(schedule, epoch, ct);
        if (records is null)
        {
            return; // a committed batch isn't fetchable yet: never sign partial data
        }

        // Once a chunk has landed, its count is fixed on-chain: chunk the same way even if
        // governance changed recordsPerTx since, or the remaining chunks would never be accepted.
        var fixedCount = await chain.RecordChunkCountAsync(epoch, ct);
        var perTx = fixedCount > 0 && records.Count > 0
            ? (records.Count + fixedCount - 1) / fixedCount
            : Math.Max(1, await chain.RecordsPerTxAsync(ct));
        var chunks = records.Count == 0 ? [[]] : records.Chunk(perTx).Select(c => (IReadOnlyList<AggregatedMetricInput>)c).ToList();
        for (var i = 0; i < chunks.Count; i++)
        {
            if (await chain.RecordChunkWrittenAsync(epoch, i, ct))
            {
                continue;
            }

            var digest = eip712.RecordsDigest(epoch, i, chunks.Count, chunks[i]);
            var digestHex = digest.ToHex(true);
            gossip.Publish(new RecordsSignature(epoch, i, chunks.Count, digestHex, signer.Address, signer.Sign(digest).ToHex(true)));
            var signatures = Sorted(gossip.Committee.RecordSignatures(epoch, i)
                .Where(r => r.ChunkCount == chunks.Count && string.Equals(r.Digest, digestHex, StringComparison.OrdinalIgnoreCase)
                            && members.Contains(r.Member, StringComparer.OrdinalIgnoreCase))
                .DistinctBy(r => r.Member.ToLowerInvariant())
                .Select(r => (r.Member, r.Signature)), threshold);
            if (signatures is not null)
            {
                try
                {
                    await relay.SubmitRecordsAsync(epoch, i, chunks.Count, chunks[i], signatures, ct);
                    Metrics.RecordChunks.Increment();
                    logger.LogInformation("Submitted records chunk {Chunk}/{Count} for epoch {Epoch}.", i + 1, chunks.Count, epoch);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Usually another member won the race (ChunkAlreadyWritten); keep going with the other chunks.
                    logger.LogInformation(ex, "Records chunk {Chunk}/{Count} for epoch {Epoch} not submitted; continuing.", i + 1, chunks.Count, epoch);
                }
            }
        }
    }

    /// <summary>
    /// The epoch's records from committed batches only, so every member computes identical bytes.
    /// Null while any committed batch can't be fetched and verified.
    /// </summary>
    internal async Task<IReadOnlyList<AggregatedMetricInput>?> BuildRecordsAsync(TelemetrySchedule schedule, ulong epoch, CancellationToken ct)
    {
        var heartbeats = new List<SignedTelemetry<HeartbeatSample>>();
        var attestations = new List<SignedTelemetry<Attestation>>();
        var selfCheckpoints = (await chain.SelfCheckpointsAsync(epoch, ct)).ToLookup(s => s.Checkpoint);
        for (ushort k = 0; k < schedule.CheckpointsPerEpoch; k++)
        {
            var committed = await chain.CheckpointAsync(epoch, k, ct);
            var sources = committed is not null
                ? [(committed.BlobAddress, committed.MerkleRoot, (string?)null)]
                : selfCheckpoints[k].Select(s => (s.BlobAddress, s.MerkleRoot, (string?)s.Node)).ToList();
            foreach (var (blob, root, node) in sources)
            {
                var bytes = await blobs.GetAsync(blob, ct);
                if (bytes is null)
                {
                    logger.LogWarning("Batch {Blob} for {Epoch}/{Window} isn't fetchable yet; holding records.", blob.ToHex(true), epoch, k);
                    return null;
                }

                var batch = TelemetryBatchCodec.DecodeCommitted(bytes, blob, root, schedule, chainId, registry);
                // A self-checkpoint may only speak for its own node.
                heartbeats.AddRange(node is null ? batch.Heartbeats
                    : batch.Heartbeats.Where(h => string.Equals(h.Payload.NodeId, node, StringComparison.OrdinalIgnoreCase)));
                if (node is null)
                {
                    attestations.AddRange(batch.Attestations);
                }
            }
        }

        var aggregator = new EpochMetricAggregator(schedule, chainId, registry);
        return heartbeats.Select(h => h.Payload.NodeId.ToLowerInvariant()).Distinct()
            .Select(n => aggregator.Aggregate(n, epoch, heartbeats, attestations))
            .Select(TelemetryAbi.ToInput)
            .Select(r => { r.Node = Nethereum.Util.AddressUtil.Current.ConvertToChecksumAddress(r.Node); return r; })
            .OrderBy(r => r.Node.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
    }

    private (List<SignedTelemetry<HeartbeatSample>>, List<SignedTelemetry<Attestation>>) Held(TelemetrySchedule schedule, ulong epoch, ushort k) =>
        ([.. gossip.Store.Heartbeats.Where(h => h.Payload.EpochIndex == epoch && h.Payload.SampleIndex < schedule.SlotsPerEpoch
                                                && schedule.CheckpointOf(h.Payload.SampleIndex) == k)],
         [.. gossip.Store.Attestations.Where(a => a.Payload.EpochIndex == epoch && a.Payload.SampleIndex < schedule.SlotsPerEpoch
                                                  && schedule.CheckpointOf(a.Payload.SampleIndex) == k)]);

    internal static long WindowEnd(TelemetrySchedule schedule, ulong epoch, ushort k) => schedule.CheckpointWindowEnd(epoch, k);

    /// <summary>Signatures ordered by ascending signer address, as the contract requires; null if fewer than <paramref name="threshold"/>.</summary>
    private static IReadOnlyList<string>? Sorted(IEnumerable<(string Member, string Signature)> signatures, int threshold)
    {
        var list = signatures.OrderBy(s => s.Member.ToLowerInvariant(), StringComparer.Ordinal).Take(Math.Max(threshold, 1)).ToList();
        return list.Count >= threshold && threshold > 0 ? [.. list.Select(s => s.Signature)] : null;
    }
}

/// <summary>Committee counters (#158 FR6).</summary>
public sealed class CommitteeMetrics
{
    public TelemetryCounter Proposals { get; } = new();

    public TelemetryCounter Endorsements { get; } = new();

    public TelemetryCounter Refusals { get; } = new();

    public TelemetryCounter Submissions { get; } = new();

    public TelemetryCounter RecordChunks { get; } = new();
}
