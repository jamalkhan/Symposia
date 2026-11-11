namespace Symposia.Network.Node.Telemetry;

/// <summary>
/// What the heartbeat pipeline last did (issue #181 FR5), for the host's Diagnostics page.
/// One instance per host, shared with the storage role; every member is safe to read concurrently.
/// </summary>
public sealed class TelemetryDiagnostics
{
    private static readonly Snapshot Empty = new(false, null, null, null);
    private Snapshot _current = Empty;

    private GossipState _gossip = GossipState.Empty;

    public Snapshot Current => Volatile.Read(ref _current);

    /// <summary>Whether heartbeats are leaving this node (#207 FR4).</summary>
    public GossipState Gossip => Volatile.Read(ref _gossip);

    /// <summary>Forgets the previous run, e.g. when the storage role restarts after a rewards reset.</summary>
    public void Reset()
    {
        Volatile.Write(ref _current, Empty);
        Volatile.Write(ref _gossip, GossipState.Empty);
    }

    /// <summary>Records one flush: how many peers it could target and the checkpoint window length it warns over.</summary>
    public void GossipTargets(int recognizedPeers, int unsupportedPeers, long windowSeconds, DateTimeOffset at) =>
        UpdateGossip(g => g with
        {
            RecognizedPeers = recognizedPeers, UnsupportedPeers = unsupportedPeers, WindowSeconds = windowSeconds, Since = g.Since ?? at,
        });

    public void GossipPushed(DateTimeOffset at) => UpdateGossip(g => g with { LastPushSucceeded = at });

    public void GossipPushFailed() => UpdateGossip(g => g with { FailedPushes = g.FailedPushes + 1 });

    /// <summary>
    /// Why heartbeats may not be reaching the committee, or null. Only once a heartbeat was accepted,
    /// other registry members exist, and a full checkpoint window has passed since gossip started.
    /// <paramref name="otherMembers"/> is the registry's other nodes; those known not to gossip
    /// (404, e.g. compute-only) are discounted, so they never raise a false warning.
    /// </summary>
    public string? GossipWarning(DateTimeOffset now, int otherMembers)
    {
        var g = Gossip;
        if (Current.LastAccepted is null || otherMembers - g.UnsupportedPeers <= 0 || g.Since is not { } since)
        {
            return null;
        }

        var window = TimeSpan.FromSeconds(Math.Max(1, g.WindowSeconds));
        if ((g.LastPushSucceeded ?? since) > now - window)
        {
            return null;
        }

        return g.RecognizedPeers == 0
            ? "No recognized telemetry gossip peers: heartbeats are stored locally but can't reach the committee, so this node won't earn rewards."
            : $"No telemetry gossip push has succeeded in the last {window.TotalMinutes:0} minutes ({g.FailedPushes} failed); heartbeats may not reach the committee.";
    }

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

    private void UpdateGossip(Func<GossipState, GossipState> change)
    {
        GossipState seen, next;
        do
        {
            seen = Volatile.Read(ref _gossip);
            next = change(seen);
        }
        while (Interlocked.CompareExchange(ref _gossip, next, seen) != seen);
    }

    public sealed record GossipState(
        int RecognizedPeers, int UnsupportedPeers, DateTimeOffset? LastPushSucceeded, long FailedPushes, long WindowSeconds, DateTimeOffset? Since)
    {
        public static readonly GossipState Empty = new(0, 0, null, 0, (long)Committee.TelemetryGossipOptions.FallbackWindow.TotalSeconds, null);
    }

    public sealed record Snapshot(bool ScheduleSynced, HeartbeatEvent? LastAccepted, HeartbeatEvent? LastRejected, string? ScheduleProblem);

    public sealed record HeartbeatEvent(ulong Epoch, uint Slot, DateTimeOffset At, string? Reason);
}
