using Symposia.Database.ComputeNode.Lifecycle;

namespace Symposia.Database.ComputeNode.Tests.Lifecycle;

internal sealed class FakeBlobBucketProvisioner : IBlobBucketProvisioner
{
    public readonly List<string> SoftDeletedBucketIds = [];

    public Task<string> ProvisionBucketAsync(string databaseId, CancellationToken cancellationToken) =>
        Task.FromResult($"bucket-{databaseId}");

    public Task SoftDeleteBucketAsync(string bucketId, CancellationToken cancellationToken)
    {
        SoftDeletedBucketIds.Add(bucketId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSafekeeperAssignmentClient : ISafekeeperAssignmentClient
{
    public int AssignCallCount;

    public Task<IReadOnlyList<string>> AssignSafekeepersAsync(string databaseId, string primaryNodeId, string region, CancellationToken cancellationToken)
    {
        AssignCallCount++;
        IReadOnlyList<string> peers = [$"{primaryNodeId}-peer-1", $"{primaryNodeId}-peer-2"];
        return Task.FromResult(peers);
    }
}

internal sealed class FakeProxyRoutingClient : IProxyRoutingClient
{
    public readonly Dictionary<string, string> Routes = [];
    public readonly HashSet<string> SuspendedDatabaseIds = [];
    public readonly HashSet<string> RemovedDatabaseIds = [];
    public readonly Dictionary<string, List<Symposia.Database.ComputeNode.Proxy.ComputeEndpoint>> Replicas = [];

    public void UpsertRoute(string databaseId, Symposia.Database.ComputeNode.Proxy.ComputeEndpoint primary, int maxConnections)
    {
        Routes[databaseId] = primary.NodeId;
        SuspendedDatabaseIds.Remove(databaseId);
    }

    public void MarkSuspended(string databaseId) => SuspendedDatabaseIds.Add(databaseId);

    public void RemoveRoute(string databaseId)
    {
        Routes.Remove(databaseId);
        RemovedDatabaseIds.Add(databaseId);
    }

    public void AddOrUpdateReplica(string databaseId, Symposia.Database.ComputeNode.Proxy.ComputeEndpoint replica)
    {
        var list = Replicas.TryGetValue(databaseId, out var existing) ? existing : Replicas[databaseId] = [];
        list.RemoveAll(r => r.NodeId == replica.NodeId);
        list.Add(replica);
    }

    public void RemoveReplica(string databaseId, string replicaNodeId)
    {
        if (Replicas.TryGetValue(databaseId, out var list))
            list.RemoveAll(r => r.NodeId == replicaNodeId);
    }

    public IReadOnlyList<Symposia.Database.ComputeNode.Proxy.ComputeEndpoint> GetReplicas(string databaseId) =>
        Replicas.TryGetValue(databaseId, out var list) ? list : [];
}
