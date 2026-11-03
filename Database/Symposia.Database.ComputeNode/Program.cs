using Symposia.Database.ComputeNode;

ComputeNodeApp.Create(args).Run();

namespace Symposia.Database.ComputeNode
{
    public sealed record UpsertRoutingRequest(Symposia.Database.ComputeNode.Proxy.ComputeEndpoint Primary, IReadOnlyList<Symposia.Database.ComputeNode.Proxy.ComputeEndpoint>? Replicas, int MaxConnections = 200);
    public sealed record ConnectRequest(string Username, string SecretHash, bool ReadOnly = false);
    public sealed record ResizeRequest(Symposia.Database.ComputeNode.Lifecycle.DatabaseSize ComputeSize);
    public sealed record CreateReplicaRequest(Symposia.Database.ComputeNode.Lifecycle.DatabaseSize ComputeSize, string DatabaseName, string? Region = null);
    public sealed record ReportLagRequest(long ReplicaLsn, long LagBytes);
    public sealed record UpgradeRequest(int TargetMajor);
}

namespace Symposia.Database.ComputeNode
{
    /// <summary>Entry point marker for WebApplicationFactory-based integration tests.</summary>
    public partial class Program;
}
