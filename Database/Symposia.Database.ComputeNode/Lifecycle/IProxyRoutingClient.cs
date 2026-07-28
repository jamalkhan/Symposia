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
}

/// <summary>Adapts the #93 <see cref="RoutingTableService"/> for in-process use by the #95 orchestrator.</summary>
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
}
