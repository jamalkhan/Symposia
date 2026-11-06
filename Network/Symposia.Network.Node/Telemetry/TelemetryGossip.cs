using System.Collections.Concurrent;

namespace Symposia.Network.Node.Telemetry;

/// <summary>
/// Transport for gossiped telemetry (issue #75 FR3). Defined at interface level only, per the
/// spec: the production mesh is expected to be libp2p gossipsub (a WAN protocol between
/// mutually distrusting operators, deliberately separate from the internal NATS bus). Every
/// implementation must verify signatures and drop replays before handing items to subscribers,
/// and must keep and forward a conflicting second payload for the same replay key (equivocation)
/// so every checkpoint-committee member converges on the same evidence.
/// </summary>
public interface ITelemetryGossip
{
    /// <summary>Publishes a signed heartbeat; returns false if it was invalid, stale or a replay.</summary>
    ValueTask<bool> PublishAsync(SignedTelemetry<HeartbeatSample> sample, CancellationToken ct = default);

    /// <summary>Publishes a signed attestation; returns false if it was invalid, stale or a replay.</summary>
    ValueTask<bool> PublishAsync(SignedTelemetry<Attestation> attestation, CancellationToken ct = default);

    event Action<SignedTelemetry<HeartbeatSample>>? HeartbeatReceived;

    event Action<SignedTelemetry<Attestation>>? AttestationReceived;
}

/// <summary>
/// Receive-time admission rules. A sample must arrive within one cadence plus
/// <paramref name="ClockSkewSeconds"/> of the time it claims, so an operator can't backfill a day
/// of heartbeats it never sent live. Its epoch must also be the current one, plus or minus one,
/// which bounds the store against future-epoch flooding.
/// </summary>
public sealed record TelemetryAdmission(TelemetrySchedule Schedule, TimeProvider Clock, long ClockSkewSeconds = 30)
{
    /// <summary>When set (#158), admission follows the chain-synced schedule instead of <see cref="Schedule"/>.</summary>
    public TelemetryScheduleSource? Source { get; init; }

    public bool Admits(ulong epoch, long claimedUnixSeconds)
    {
        var schedule = Source?.Current ?? Schedule;
        var now = Clock.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - claimedUnixSeconds) > schedule.CadenceSeconds + ClockSkewSeconds)
        {
            return false;
        }

        var current = schedule.EpochAt(now);
        return epoch + 1 >= current && epoch <= current + 1;
    }
}

/// <summary>
/// Single-process gossip: validates, de-duplicates by replay key and fans out to subscribers.
/// Used by tests and single-host deployments, and keeps everything it has seen for the
/// checkpoint builder to read (<see cref="Heartbeats"/>, <see cref="Attestations"/>).
/// A second, *different* payload under an existing replay key is kept and forwarded once
/// (equivocation evidence; the aggregator drops such slots). Further copies are dropped, so
/// each key holds at most two entries.
/// </summary>
public sealed class InMemoryTelemetryGossip(ulong chainId, string registry, TelemetryAdmission? admission = null)
    : ITelemetryGossip
{
    private const int MaxCopiesPerKey = 2;

    private readonly ConcurrentDictionary<(string, ulong, uint), List<SignedTelemetry<HeartbeatSample>>> _heartbeats = new();
    private readonly ConcurrentDictionary<(string, string, ulong, uint), List<SignedTelemetry<Attestation>>> _attestations = new();

    public event Action<SignedTelemetry<HeartbeatSample>>? HeartbeatReceived;

    public event Action<SignedTelemetry<Attestation>>? AttestationReceived;

    public IReadOnlyCollection<SignedTelemetry<HeartbeatSample>> Heartbeats => Snapshot(_heartbeats);

    public IReadOnlyCollection<SignedTelemetry<Attestation>> Attestations => Snapshot(_attestations);

    public ValueTask<bool> PublishAsync(SignedTelemetry<HeartbeatSample> sample, CancellationToken ct = default)
    {
        if ((admission is not null && !admission.Admits(sample.Payload.EpochIndex, sample.Payload.UnixSeconds))
            || !TelemetrySigning.Verify(sample, chainId, registry)
            || !TryStore(_heartbeats, sample.Payload.ReplayKey, sample))
        {
            return ValueTask.FromResult(false);
        }

        HeartbeatReceived?.Invoke(sample);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> PublishAsync(SignedTelemetry<Attestation> attestation, CancellationToken ct = default)
    {
        if ((admission is not null && !admission.Admits(attestation.Payload.EpochIndex, attestation.Payload.UnixSeconds))
            || !TelemetrySigning.Verify(attestation, chainId, registry)
            || !TryStore(_attestations, attestation.Payload.ReplayKey, attestation))
        {
            return ValueTask.FromResult(false);
        }

        AttestationReceived?.Invoke(attestation);
        return ValueTask.FromResult(true);
    }

    /// <summary>Drops everything before <paramref name="epoch"/> once its aggregate is final.</summary>
    public void PruneBefore(ulong epoch)
    {
        foreach (var key in _heartbeats.Keys.Where(k => k.Item2 < epoch))
        {
            _heartbeats.TryRemove(key, out _);
        }

        foreach (var key in _attestations.Keys.Where(k => k.Item3 < epoch))
        {
            _attestations.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Stores the first copy for a key, plus one conflicting copy (a different payload, which is
    /// equivocation). Returns false for exact duplicates (same payload) and anything beyond that.
    /// </summary>
    private static bool TryStore<TKey, T>(ConcurrentDictionary<TKey, List<SignedTelemetry<T>>> store, TKey key, SignedTelemetry<T> item)
        where TKey : notnull
        where T : class
    {
        var copies = store.GetOrAdd(key, _ => []);
        lock (copies)
        {
            if (copies.Count >= MaxCopiesPerKey || copies.Any(c => Equals(c.Payload, item.Payload)))
            {
                return false;
            }

            copies.Add(item);
            return true;
        }
    }

    private static List<SignedTelemetry<T>> Snapshot<TKey, T>(ConcurrentDictionary<TKey, List<SignedTelemetry<T>>> store)
        where TKey : notnull
        where T : class
    {
        var all = new List<SignedTelemetry<T>>();
        foreach (var copies in store.Values)
        {
            lock (copies)
            {
                all.AddRange(copies);
            }
        }

        return all;
    }
}
