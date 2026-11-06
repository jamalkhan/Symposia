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
    EndpointNotDialable,
}

/// <param name="ExpectedAddress">The registry's address for this endpoint, or null for a seed peer.</param>
/// <param name="Address">The peer's verified address, or its claimed/expected address if verification failed.</param>
public sealed record PeerEntry(
    string Endpoint,
    string? ExpectedAddress,
    string? Address,
    PeerStatus Status,
    PeerFailureReason Reason,
    DateTimeOffset LastAttempt);

/// <summary>
/// In-memory view of known peers (issue #129 FR-C1/FR-D4). Replaced wholesale
/// by each completed discovery cycle, so peers that left the registry or moved
/// endpoints drop out instead of lingering as Recognized. Never persisted.
/// </summary>
public sealed class PeerTable
{
    private volatile IReadOnlyList<PeerEntry> _peers = [];

    public void Replace(IEnumerable<PeerEntry> entries) =>
        _peers = entries.OrderBy(p => p.Endpoint, StringComparer.Ordinal).ThenBy(p => p.ExpectedAddress, StringComparer.Ordinal).ToList();

    public IReadOnlyList<PeerEntry> Snapshot() => _peers;

    public IReadOnlyList<PeerEntry> Recognized() => _peers.Where(p => p.Status == PeerStatus.Recognized).ToList();
}
