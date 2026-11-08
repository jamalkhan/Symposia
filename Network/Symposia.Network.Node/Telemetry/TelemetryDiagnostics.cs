namespace Symposia.Network.Node.Telemetry;

/// <summary>
/// What the heartbeat pipeline last did (issue #181 FR5), for the host's Diagnostics page.
/// One instance per host, shared with the storage role; every member is safe to read concurrently.
/// </summary>
public sealed class TelemetryDiagnostics
{
    private static readonly Snapshot Empty = new(false, null, null, null);
    private Snapshot _current = Empty;

    public Snapshot Current => Volatile.Read(ref _current);

    /// <summary>Forgets the previous run, e.g. when the storage role restarts after a rewards reset.</summary>
    public void Reset() => Volatile.Write(ref _current, Empty);

    public void ScheduleSynced() => Update(s => s with { ScheduleSynced = true, ScheduleProblem = null });

    public void ScheduleInvalid(string problem) => Update(s => s with { ScheduleProblem = problem });

    public void Accepted(ulong epoch, uint slot, DateTimeOffset at) => Update(s => s with { LastAccepted = new(epoch, slot, at, null) });

    public void Rejected(ulong epoch, uint slot, DateTimeOffset at, string reason) =>
        Update(s => s with { LastRejected = new(epoch, slot, at, reason) });

    private void Update(Func<Snapshot, Snapshot> change)
    {
        Snapshot seen, next;
        do
        {
            seen = Volatile.Read(ref _current);
            next = change(seen);
        }
        while (Interlocked.CompareExchange(ref _current, next, seen) != seen);
    }

    public sealed record Snapshot(bool ScheduleSynced, HeartbeatEvent? LastAccepted, HeartbeatEvent? LastRejected, string? ScheduleProblem);

    public sealed record HeartbeatEvent(ulong Epoch, uint Slot, DateTimeOffset At, string? Reason);
}
