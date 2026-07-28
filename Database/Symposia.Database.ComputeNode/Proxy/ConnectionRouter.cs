namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>
/// Ties together auth, admission, routing-table lookup, read/write split, and wake-on-connect
/// into the single decision path every incoming client connection passes through (the #93
/// architectural plan's "steady-state connection path": auth -> route resolve -> pooled backend
/// checkout). Rejection happens strictly in auth/admission order so an invalid credential never
/// reaches the routing table or a compute node (AC3, AUTH-02).
/// </summary>
public sealed class ConnectionRouter(
    AuthCacheService authCache,
    ConnectionAdmissionService admission,
    RoutingTableService routingTable,
    WakeOnConnectCoordinator wakeOnConnect)
{
    public async Task<ConnectionDecision> RouteAsync(ConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (!authCache.Authenticate(request.DatabaseId, request.Username, request.SecretHash))
            return ConnectionDecision.AuthRejected();

        var entry = routingTable.Get(request.DatabaseId);
        if (entry is null)
            return ConnectionDecision.NoRoute($"No routing entry exists for database '{request.DatabaseId}'.");

        if (!admission.TryAdmit(request.DatabaseId, entry.MaxConnections))
            return ConnectionDecision.LimitExceeded();

        try
        {
            if (entry.Status == RoutingStatus.Suspended)
            {
                var endpoint = await wakeOnConnect.WakeAsync(request.DatabaseId, cancellationToken);
                return ConnectionDecision.Routed(endpoint);
            }

            if (request.ReadOnly && entry.Replicas.Count > 0)
                return ConnectionDecision.Routed(entry.Replicas[0]);

            return ConnectionDecision.Routed(entry.Primary);
        }
        catch
        {
            admission.Release(request.DatabaseId);
            throw;
        }
    }

    /// <summary>Releases the admission slot when a client disconnects (paired with a successful <see cref="RouteAsync"/>).</summary>
    public void ReleaseConnection(string databaseId) => admission.Release(databaseId);
}
