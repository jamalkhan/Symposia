namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>
/// In-process stand-in for the #93 architectural plan's JetStream KV routing table (FR4). Real
/// deployments watch a shared, versioned KV bucket kept current by provisioning (#95), migration
/// (#92), and suspend/resume events; this control-plane skeleton owns the same read/update/version
/// semantics for a single proxy shard, out of scope being the distributed sync itself.
/// </summary>
public sealed class RoutingTableService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RoutingEntry> _entries = [];

    public RoutingEntry Upsert(string databaseId, ComputeEndpoint primary, IReadOnlyList<ComputeEndpoint>? replicas = null, int maxConnections = 200)
    {
        lock (_gate)
        {
            var entry = new RoutingEntry(databaseId, Version: 1, RoutingStatus.Active, primary, replicas ?? [], maxConnections);
            _entries[databaseId] = entry;
            return entry;
        }
    }

    public RoutingEntry? Get(string databaseId)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(databaseId);
        }
    }

    /// <summary>Migration/failover cutover (FR5): points the database at a new primary and marks it active.</summary>
    public RoutingEntry? UpdatePrimary(string databaseId, ComputeEndpoint newPrimary)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(databaseId, out var current))
                return null;

            var updated = current.WithPrimary(newPrimary);
            _entries[databaseId] = updated;
            return updated;
        }
    }

    public RoutingEntry? MarkStatus(string databaseId, RoutingStatus status)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(databaseId, out var current))
                return null;

            var updated = current.WithStatus(status);
            _entries[databaseId] = updated;
            return updated;
        }
    }

    public RoutingEntry SetReplicas(string databaseId, IReadOnlyList<ComputeEndpoint> replicas)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(databaseId, out var current))
                throw new InvalidOperationException($"No routing entry exists for database '{databaseId}'.");

            var updated = current with { Replicas = replicas, Version = current.Version + 1 };
            _entries[databaseId] = updated;
            return updated;
        }
    }
}
