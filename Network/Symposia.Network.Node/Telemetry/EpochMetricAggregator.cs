namespace Symposia.Network.Node.Telemetry;

/// <summary>Per-field aggregated values; mirrors <c>TelemetryCheckpointRegistry.MetricValues</c>.</summary>
public sealed record MetricValues(
    uint UptimeBps,
    ulong ReadThroughputKBps,
    ulong WriteThroughputKBps,
    ulong BandwidthInKbps,
    ulong BandwidthOutKbps,
    uint LatencyMs,
    uint RetrievalMs)
{
    public static readonly MetricValues Empty = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// One node's aggregated epoch record; mirrors <c>TelemetryCheckpointRegistry.AggregatedMetricInput</c>
/// (the contract derives compliance bps, eligibility and the divergence flag itself).
/// </summary>
public sealed record AggregatedMetricRecord(
    string NodeId,
    ulong EpochIndex,
    MetricValues SelfReported,
    MetricValues VerifierAttested,
    uint HeartbeatsReceived,
    uint HeartbeatsExpected,
    ushort AttestationCount,
    ushort FirstCheckpoint,
    ushort LastCheckpoint)
{
    public ushort HeartbeatComplianceBps =>
        HeartbeatsExpected == 0 ? (ushort)0 : (ushort)((ulong)HeartbeatsReceived * 10_000 / HeartbeatsExpected);

    /// <summary>"At least one valid metric report this epoch" (#75 FR9).</summary>
    public bool Eligible => HeartbeatsReceived > 0;
}

/// <summary>Epoch timing shared by the emitter and aggregator; must match the chain's config.</summary>
public sealed record TelemetrySchedule(long GenesisUnixSeconds, long EpochSeconds, long CadenceSeconds = 300, int CheckpointsPerEpoch = 24)
{
    public uint SlotsPerEpoch => (uint)(EpochSeconds / CadenceSeconds);

    public ulong EpochAt(long unixSeconds) => (ulong)Math.Max(0, (unixSeconds - GenesisUnixSeconds) / EpochSeconds);

    public long EpochStart(ulong epoch) => GenesisUnixSeconds + (long)epoch * EpochSeconds;

    public uint SlotAt(long unixSeconds) => (uint)((unixSeconds - EpochStart(EpochAt(unixSeconds))) / CadenceSeconds);

    /// <summary>End of checkpoint window <paramref name="k"/>, matching <c>TelemetryCheckpointRegistry.checkpointWindowEnd</c>.</summary>
    public long CheckpointWindowEnd(ulong epoch, ushort k) => EpochStart(epoch) + (k + 1) * EpochSeconds / CheckpointsPerEpoch;

    /// <summary>The checkpoint window a slot falls in.</summary>
    public ushort CheckpointOf(uint slot) => (ushort)((ulong)slot * (ulong)CheckpointsPerEpoch / SlotsPerEpoch);
}

/// <summary>
/// The schedule heartbeats are stamped with. Starts from configuration; when the telemetry
/// network is on (#158), the committee service replaces it with the EpochController's values,
/// so a node's samples always land in the slots the chain counts.
/// </summary>
public sealed class TelemetryScheduleSource(TelemetrySchedule initial)
{
    private volatile TelemetrySchedule _current = initial;

    public TelemetrySchedule Current
    {
        get => _current;
        set => _current = value;
    }
}

/// <summary>
/// Builds a node's <see cref="AggregatedMetricRecord"/> from the epoch's signed samples and
/// attestations (issue #75 FR5/FR6). Invalid signatures, other nodes' or epochs' samples,
/// out-of-range or timestamp-inconsistent slots, and replays are dropped before aggregation.
/// Exact duplicates collapse to one, and equivocated slots (same replay key, different signed
/// payloads) are dropped entirely, so input order and which copy arrived first never matter.
/// Self-reported and verifier-attested values are aggregated separately and never merged, so a
/// discrepancy stays visible to #79.
/// </summary>
public sealed class EpochMetricAggregator(TelemetrySchedule schedule, ulong chainId, string registry)
{
    /// <param name="expectedHeartbeats">
    /// Slots the node was expected to report in: <see cref="TelemetrySchedule.SlotsPerEpoch"/> for a
    /// full epoch, fewer for a node that became active mid-epoch (QA open question 7).
    /// </param>
    public AggregatedMetricRecord Aggregate(
        string nodeId,
        ulong epoch,
        IEnumerable<SignedTelemetry<HeartbeatSample>> samples,
        IEnumerable<SignedTelemetry<Attestation>> attestations,
        uint? expectedHeartbeats = null)
    {
        var expected = expectedHeartbeats ?? schedule.SlotsPerEpoch;
        var heartbeats = samples
            .Where(s => string.Equals(s.Payload.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
                        && s.Payload.EpochIndex == epoch
                        && s.Payload.SampleIndex < schedule.SlotsPerEpoch
                        && SlotMatches(epoch, s.Payload.SampleIndex, s.Payload.UnixSeconds)
                        && TelemetrySigning.Verify(s, chainId, registry))
            .Select(s => s.Payload)
            .GroupBy(p => p.ReplayKey)
            .Where(NotEquivocated)
            .Select(g => g.First())
            .OrderBy(p => p.SampleIndex)
            .ToList();

        var attested = attestations
            .Where(a => string.Equals(a.Payload.TargetNodeId, nodeId, StringComparison.OrdinalIgnoreCase)
                        && a.Payload.EpochIndex == epoch
                        && !string.Equals(a.Payload.VerifierId, nodeId, StringComparison.OrdinalIgnoreCase)
                        && TelemetrySigning.Verify(a, chainId, registry))
            .Select(a => a.Payload)
            .GroupBy(p => p.ReplayKey)
            .Where(NotEquivocated)
            .Select(g => g.First())
            .OrderBy(p => p.VerifierId.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(p => p.SampleIndex)
            .ToList();

        var received = (uint)Math.Min(heartbeats.Count, expected);
        var self = heartbeats.Count == 0
            ? MetricValues.Empty
            : new MetricValues(
                expected == 0 ? 0 : (uint)((ulong)received * 10_000 / expected),
                RobustAggregator.Aggregate(heartbeats.Select(h => h.ReadThroughputKBps).ToList()),
                RobustAggregator.Aggregate(heartbeats.Select(h => h.WriteThroughputKBps).ToList()),
                RobustAggregator.Aggregate(heartbeats.Select(h => h.BandwidthInKbps).ToList()),
                RobustAggregator.Aggregate(heartbeats.Select(h => h.BandwidthOutKbps).ToList()),
                (uint)RobustAggregator.Aggregate(heartbeats.Select(h => (ulong)h.LatencyMs).ToList()),
                (uint)RobustAggregator.Aggregate(heartbeats.Select(h => (ulong)h.RetrievalMs).ToList()));

        var reachable = attested.Where(a => a.Reachable).ToList();
        var verifier = attested.Count == 0
            ? MetricValues.Empty
            : MetricValues.Empty with
            {
                UptimeBps = (uint)(reachable.Count * 10_000L / attested.Count),
                LatencyMs = (uint)RobustAggregator.Aggregate(reachable.Select(a => (ulong)a.LatencyMs).ToList()),
                RetrievalMs = (uint)RobustAggregator.Aggregate(reachable.Select(a => (ulong)a.RetrievalMs).ToList()),
            };

        var first = heartbeats.Count == 0 ? (ushort)0 : schedule.CheckpointOf(heartbeats[0].SampleIndex);
        var last = heartbeats.Count == 0 ? (ushort)0 : schedule.CheckpointOf(heartbeats[^1].SampleIndex);
        return new AggregatedMetricRecord(nodeId, epoch, self, verifier, received, expected,
            (ushort)Math.Min(attested.Count, ushort.MaxValue), first, last);
    }

    /// <summary>
    /// A replay key signed with two different payloads is equivocation. The slot is dropped
    /// (counted as missed) instead of picking one copy, so every committee member reaches the
    /// same record no matter which copy it saw first, given gossip forwards both.
    /// </summary>
    private static bool NotEquivocated<TKey, T>(IGrouping<TKey, T> group) => group.Distinct().Count() == 1;

    private bool SlotMatches(ulong epoch, uint slot, long unixSeconds)
    {
        var slotStart = schedule.EpochStart(epoch) + slot * schedule.CadenceSeconds;
        return unixSeconds >= slotStart && unixSeconds < slotStart + schedule.CadenceSeconds;
    }
}
