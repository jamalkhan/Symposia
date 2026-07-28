namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>
/// A tenant database's current routing assignment, mirroring the #93 architectural plan's
/// JetStream KV record. <see cref="Version"/> is used for optimistic staleness checks: on a
/// backend connection failure the proxy re-reads the entry and only retries once the version
/// has advanced past what it had cached, avoiding retry storms against a still-dead node.
/// </summary>
public sealed record RoutingEntry(
    string DatabaseId,
    long Version,
    RoutingStatus Status,
    ComputeEndpoint Primary,
    IReadOnlyList<ComputeEndpoint> Replicas,
    int MaxConnections)
{
    public RoutingEntry WithStatus(RoutingStatus status) =>
        this with { Status = status, Version = Version + 1 };

    public RoutingEntry WithPrimary(ComputeEndpoint primary) =>
        this with { Primary = primary, Status = RoutingStatus.Active, Version = Version + 1 };
}
