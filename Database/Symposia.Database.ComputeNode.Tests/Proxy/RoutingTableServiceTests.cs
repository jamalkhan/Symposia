using Symposia.Database.ComputeNode.Proxy;

namespace Symposia.Database.ComputeNode.Tests.Proxy;

public sealed class RoutingTableServiceTests
{
    [Fact]
    public void UpdatePrimary_IncrementsVersion()
    {
        var table = new RoutingTableService();
        var initial = table.Upsert("db-1", new ComputeEndpoint("primary-1", "10.0.0.1", 5432));

        var updated = table.UpdatePrimary("db-1", new ComputeEndpoint("primary-2", "10.0.0.2", 5432));

        Assert.Equal(initial.Version + 1, updated!.Version);
        Assert.Equal("primary-2", updated.Primary.NodeId);
        Assert.Equal(RoutingStatus.Active, updated.Status);
    }

    [Fact]
    public void MarkStatus_UnknownDatabase_ReturnsNull()
    {
        var table = new RoutingTableService();

        Assert.Null(table.MarkStatus("missing", RoutingStatus.Suspended));
    }

    [Fact]
    public void SetReplicas_ReplacesReplicaList()
    {
        var table = new RoutingTableService();
        table.Upsert("db-1", new ComputeEndpoint("primary-1", "10.0.0.1", 5432));

        var updated = table.SetReplicas("db-1", [new ComputeEndpoint("replica-1", "10.0.0.9", 5432)]);

        Assert.Single(updated.Replicas);
        Assert.Equal("replica-1", updated.Replicas[0].NodeId);
    }
}
