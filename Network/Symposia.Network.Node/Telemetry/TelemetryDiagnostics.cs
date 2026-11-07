namespace Symposia.Network.Node.Telemetry;

/// <summary>
/// What the heartbeat pipeline last did (issue #181 FR5), for the host's Diagnostics page.
/// One instance per host, shared with the storage role; every member is safe to read concurrently.
/// </summary>
public sealed class TelemetryDiagnostics
{
    private volatile Snapshot _current = new(false, null, null);

    public Snapshot Current => _current;

    public void ScheduleSynced() => _current = _current with { ScheduleSynced = true };

    public void Accepted(ulong epoch, uint slot, DateTimeOffset at) => _current = _current with { LastAccepted = new(epoch, slot, at, null) };

    public void Rejected(ulong epoch, uint slot, DateTimeOffset at, string reason) =>
        _current = _current with { LastRejected = new(epoch, slot, at, reason) };

    public sealed record Snapshot(bool ScheduleSynced, HeartbeatEvent? LastAccepted, HeartbeatEvent? LastRejected);

    public sealed record HeartbeatEvent(ulong Epoch, uint Slot, DateTimeOffset At, string? Reason);
}
