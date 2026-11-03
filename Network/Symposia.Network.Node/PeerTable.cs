using System.Collections.Concurrent;

namespace Symposia.Network.Node;

public enum PeerStatus
{
    Recognized,
    Unrecognized,
}

public enum PeerFailureReason
{
    None,
    SignatureInvalid,
    AddressMismatch,
    NotRegistered,
    Unreachable,
    Timeout,
}

/// <param name="Address">The peer's verified address, or its claimed/expected address if verification failed.</param>
public sealed record PeerEntry(
    string Endpoint,
    string? Address,
    PeerStatus Status,
    PeerFailureReason Reason,
    DateTimeOffset LastAttempt);

/// <summary>
/// In-memory view of known peers keyed by endpoint (issue #129 FR-C1/FR-D4).
/// Refreshed each discovery cycle; never persisted.
/// </summary>
public sealed class PeerTable
{
    private readonly ConcurrentDictionary<string, PeerEntry> _peers = new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(PeerEntry entry) => _peers[entry.Endpoint] = entry;

    public IReadOnlyList<PeerEntry> Snapshot() => _peers.Values.OrderBy(p => p.Endpoint, StringComparer.Ordinal).ToList();

    public IReadOnlyList<PeerEntry> Recognized() => Snapshot().Where(p => p.Status == PeerStatus.Recognized).ToList();
}
