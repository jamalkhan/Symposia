using Symposia.Database.ComputeNode.Proxy;

namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// The orchestrator's write side of #93's routing table (FR4): a routing entry must exist before
/// a connection string is considered usable, and must be removed immediately on deletion.
/// </summary>
public interface IProxyRoutingClient
{
    void UpsertRoute(string databaseId, ComputeEndpoint primary, int maxConnections);

    void MarkSuspended(string databaseId);

    void RemoveRoute(string databaseId);

    /// <summary>Adds or replaces a replica entry in the database's routing-table `replicas[]` array (#96 FR10).</summary>
    void AddOrUpdateReplica(string databaseId, ComputeEndpoint replica);

    /// <summary>Removes a single replica entry, leaving the primary and other replicas untouched (#96 FR8).</summary>
    void RemoveReplica(string databaseId, string replicaNodeId);

    IReadOnlyList<ComputeEndpoint> GetReplicas(string databaseId);
}

/// <summary>Adapts the #93 <see cref="RoutingTableService"/> for in-process use by the #95/#96 orchestrators.</summary>
public sealed class ProxyRoutingClient(RoutingTableService routingTable) : IProxyRoutingClient
{
    public void UpsertRoute(string databaseId, ComputeEndpoint primary, int maxConnections) =>
        routingTable.Upsert(databaseId, primary, maxConnections: maxConnections);

    public void MarkSuspended(string databaseId) =>
        routingTable.MarkStatus(databaseId, RoutingStatus.Suspended);

    public void RemoveRoute(string databaseId)
    {
        // #93's RoutingTableService has no explicit delete; marking Unreachable achieves the same
        // "never route to a decommissioned database" contract without adding a removal API #93
        // doesn't otherwise need.
        routingTable.MarkStatus(databaseId, RoutingStatus.Unreachable);
    }

    public void AddOrUpdateReplica(string databaseId, ComputeEndpoint replica)
    {
        var current = GetReplicas(databaseId).Where(r => r.NodeId != replica.NodeId).ToList();
        current.Add(replica);
        routingTable.SetReplicas(databaseId, current);
    }

    public void RemoveReplica(string databaseId, string replicaNodeId)
    {
        var remaining = GetReplicas(databaseId).Where(r => r.NodeId != replicaNodeId).ToList();
        routingTable.SetReplicas(databaseId, remaining);
    }

    public IReadOnlyList<ComputeEndpoint> GetReplicas(string databaseId) =>
        routingTable.Get(databaseId)?.Replicas ?? [];
}
