using System.Collections.Concurrent;

namespace Symposia.Network.Node.Telemetry;

/// <summary>
/// Transport for gossiped telemetry (issue #75 FR3). Defined at interface level only, per the
/// spec: the production mesh is expected to be libp2p gossipsub (a WAN protocol between
/// mutually distrusting operators, deliberately separate from the internal NATS bus). Every
/// implementation must verify signatures and drop replays before handing items to subscribers.
/// </summary>
public interface ITelemetryGossip
{
    /// <summary>Publishes a signed heartbeat; returns false if it was invalid or a replay.</summary>
    ValueTask<bool> PublishAsync(SignedTelemetry<HeartbeatSample> sample, CancellationToken ct = default);

    /// <summary>Publishes a signed attestation; returns false if it was invalid or a replay.</summary>
    ValueTask<bool> PublishAsync(SignedTelemetry<Attestation> attestation, CancellationToken ct = default);

    event Action<SignedTelemetry<HeartbeatSample>>? HeartbeatReceived;

    event Action<SignedTelemetry<Attestation>>? AttestationReceived;
}

/// <summary>
/// Single-process gossip: validates, de-duplicates by replay key and fans out to subscribers.
/// Used by tests and single-host deployments, and keeps everything it has seen for the
/// checkpoint builder to read (<see cref="Heartbeats"/>, <see cref="Attestations"/>).
/// </summary>
public sealed class InMemoryTelemetryGossip(ulong chainId, string registry) : ITelemetryGossip
{
    private readonly ConcurrentDictionary<(string, ulong, uint), SignedTelemetry<HeartbeatSample>> _heartbeats = new();
    private readonly ConcurrentDictionary<(string, string, ulong, uint), SignedTelemetry<Attestation>> _attestations = new();

    public event Action<SignedTelemetry<HeartbeatSample>>? HeartbeatReceived;

    public event Action<SignedTelemetry<Attestation>>? AttestationReceived;

    public IReadOnlyCollection<SignedTelemetry<HeartbeatSample>> Heartbeats => _heartbeats.Values.ToList();

    public IReadOnlyCollection<SignedTelemetry<Attestation>> Attestations => _attestations.Values.ToList();

    public ValueTask<bool> PublishAsync(SignedTelemetry<HeartbeatSample> sample, CancellationToken ct = default)
    {
        if (!TelemetrySigning.Verify(sample, chainId, registry) || !_heartbeats.TryAdd(sample.Payload.ReplayKey, sample))
        {
            return ValueTask.FromResult(false);
        }

        HeartbeatReceived?.Invoke(sample);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> PublishAsync(SignedTelemetry<Attestation> attestation, CancellationToken ct = default)
    {
        if (!TelemetrySigning.Verify(attestation, chainId, registry) || !_attestations.TryAdd(attestation.Payload.ReplayKey, attestation))
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
}
