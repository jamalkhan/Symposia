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
    /// <summary>Publishes a signed heartbeat; the result says why it was refused if it was invalid, stale or a replay.</summary>
    ValueTask<TelemetryPublishResult> PublishAsync(SignedTelemetry<HeartbeatSample> sample, CancellationToken ct = default);

    /// <summary>Publishes a signed attestation; the result says why it was refused if it was invalid, stale or a replay.</summary>
    ValueTask<TelemetryPublishResult> PublishAsync(SignedTelemetry<Attestation> attestation, CancellationToken ct = default);

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

    public bool Admits(ulong epoch, long claimedUnixSeconds) => Check(epoch, claimedUnixSeconds).Accepted;

    /// <summary>Like <see cref="Admits"/>, but says which rule refused the item (#183).</summary>
    public TelemetryPublishResult Check(ulong epoch, long claimedUnixSeconds)
    {
        var schedule = Source?.Current ?? Schedule;
        var now = Clock.GetUtcNow().ToUnixTimeSeconds();
        var window = schedule.CadenceSeconds + ClockSkewSeconds;
        var behind = now - claimedUnixSeconds;
        if (Math.Abs(behind) > window)
        {
            return TelemetryPublishResult.Reject(TelemetryRejection.ClockSkew,
                $"claimed time is {Math.Abs(behind)} s {(behind > 0 ? "behind" : "ahead of")} this node's clock (allowed ±{window} s)");
        }

        var current = schedule.EpochAt(now);
        return epoch + 1 >= current && epoch <= current + 1
            ? TelemetryPublishResult.Ok
            : TelemetryPublishResult.Reject(TelemetryRejection.EpochOutOfRange, $"epoch {epoch} is not within one of the current epoch {current}");
    }
}

/// <summary>Why gossip refused a telemetry item (#183). <see cref="None"/> means it was accepted.</summary>
public enum TelemetryRejection
{
    None,
    ClockSkew,
    EpochOutOfRange,
    InvalidSignature,
    Duplicate,
    ReplayLimit,
}

/// <summary>
/// Outcome of publishing a telemetry item (#183). Deliberately not convertible to <c>bool</c>, so a
/// caller can't silently drop the reason; <see cref="Detail"/> is only built on rejection.
/// </summary>
public readonly record struct TelemetryPublishResult(TelemetryRejection Rejection, string? Detail = null)
{
    public static TelemetryPublishResult Ok => default;

    public bool Accepted => Rejection == TelemetryRejection.None;

    public static TelemetryPublishResult Reject(TelemetryRejection rejection, string? detail = null) => new(rejection, detail);

    /// <summary>Operator-facing text, e.g. for the Diagnostics page.</summary>
    public string Describe() => Rejection switch
    {
        TelemetryRejection.None => "accepted",
        TelemetryRejection.ClockSkew => $"rejected by gossip admission: {Detail ?? "claimed time is outside the allowed clock skew"}",
        TelemetryRejection.EpochOutOfRange => $"rejected by gossip admission: {Detail ?? "wrong epoch"}",
        TelemetryRejection.InvalidSignature => "rejected by gossip: signature does not verify for this chain and registry",
        TelemetryRejection.Duplicate => "rejected by gossip: an identical copy was already seen",
        TelemetryRejection.ReplayLimit => "rejected by gossip: this epoch and slot already hold the maximum two copies",
        _ => $"rejected by gossip ({Rejection})",
    };
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

    public ValueTask<TelemetryPublishResult> PublishAsync(SignedTelemetry<HeartbeatSample> sample, CancellationToken ct = default)
    {
        // Same rules, same order as before #183; the first one that fails is the reason.
        var result = admission?.Check(sample.Payload.EpochIndex, sample.Payload.UnixSeconds) ?? TelemetryPublishResult.Ok;
        if (result.Accepted && !TelemetrySigning.Verify(sample, chainId, registry))
        {
            result = TelemetryPublishResult.Reject(TelemetryRejection.InvalidSignature);
        }

        if (result.Accepted)
        {
            result = TryStore(_heartbeats, sample.Payload.ReplayKey, sample);
        }

        if (result.Accepted)
        {
            HeartbeatReceived?.Invoke(sample);
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<TelemetryPublishResult> PublishAsync(SignedTelemetry<Attestation> attestation, CancellationToken ct = default)
    {
        // Same rules, same order as before #183; the first one that fails is the reason.
        var result = admission?.Check(attestation.Payload.EpochIndex, attestation.Payload.UnixSeconds) ?? TelemetryPublishResult.Ok;
        if (result.Accepted && !TelemetrySigning.Verify(attestation, chainId, registry))
        {
            result = TelemetryPublishResult.Reject(TelemetryRejection.InvalidSignature);
        }

        if (result.Accepted)
        {
            result = TryStore(_attestations, attestation.Payload.ReplayKey, attestation);
        }

        if (result.Accepted)
        {
            AttestationReceived?.Invoke(attestation);
        }

        return ValueTask.FromResult(result);
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
    /// equivocation). Refuses exact duplicates (same payload) first, then anything beyond that.
    /// </summary>
    private static TelemetryPublishResult TryStore<TKey, T>(ConcurrentDictionary<TKey, List<SignedTelemetry<T>>> store, TKey key, SignedTelemetry<T> item)
        where TKey : notnull
        where T : class
    {
        var copies = store.GetOrAdd(key, _ => []);
        lock (copies)
        {
            if (copies.Any(c => Equals(c.Payload, item.Payload)))
            {
                return TelemetryPublishResult.Reject(TelemetryRejection.Duplicate);
            }

            if (copies.Count >= MaxCopiesPerKey)
            {
                return TelemetryPublishResult.Reject(TelemetryRejection.ReplayLimit);
            }

            copies.Add(item);
            return TelemetryPublishResult.Ok;
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
