namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>
/// Enforces the per-database max-connection ceiling (FR7, AC6/AC11; default 200 per
/// database-billing.md). Counts client-facing logical connections, independent of how many
/// backend connections the pooler multiplexes them down to (LIM-03) -- consistent-hash sharding
/// (per the #93 arch) makes this a local counter check rather than a distributed one.
/// </summary>
public sealed class ConnectionAdmissionService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _activeConnections = [];

    public bool TryAdmit(string databaseId, int maxConnections)
    {
        lock (_gate)
        {
            var current = _activeConnections.GetValueOrDefault(databaseId);
            if (current >= maxConnections)
                return false;

            _activeConnections[databaseId] = current + 1;
            return true;
        }
    }

    public void Release(string databaseId)
    {
        lock (_gate)
        {
            if (_activeConnections.TryGetValue(databaseId, out var current) && current > 0)
                _activeConnections[databaseId] = current - 1;
        }
    }

    public int GetActiveCount(string databaseId)
    {
        lock (_gate)
        {
            return _activeConnections.GetValueOrDefault(databaseId);
        }
    }
}
