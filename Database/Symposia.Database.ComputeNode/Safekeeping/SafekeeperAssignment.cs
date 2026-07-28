namespace Symposia.Database.ComputeNode.Safekeeping;

public enum QuorumHealth
{
    /// <summary>All 3 quorum members (primary + 2 peers) are within budget; commits proceed normally.</summary>
    Healthy,

    /// <summary>A peer breached RTT/failed and a qualifying replacement is being sought or caught up.</summary>
    Degraded,

    /// <summary>No qualifying replacement peer was available; quorum cannot be restored to 3 members.</summary>
    AwaitingQualifyingPeer,
}

/// <summary>
/// The coordination service's durable record of a primary's assigned safekeeper peer set
/// (Arch data model: `safekeeper_assignments(db_id, primary_node_id, peer_node_ids[2], region, assigned_at, status)`).
/// </summary>
public sealed record SafekeeperAssignment(
    string DatabaseId,
    string PrimaryNodeId,
    IReadOnlyList<string> PeerNodeIds,
    string Region,
    DateTimeOffset AssignedAt,
    QuorumHealth Status);
