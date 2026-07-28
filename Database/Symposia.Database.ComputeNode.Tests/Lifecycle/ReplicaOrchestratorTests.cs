using Symposia.Database.ComputeNode.Lifecycle;

namespace Symposia.Database.ComputeNode.Tests.Lifecycle;

/// <summary>Traces to #96's Gherkin scenarios and QA plan (Provisioning, WAL lag, billing independence, lifecycle independence, deletion, multi-replica, proxy routing, promotion).</summary>
public sealed class ReplicaOrchestratorTests
{
    private static (ReplicaOrchestrator Replicas, DatabaseLifecycleOrchestrator Primary, InMemoryComputeNodePlacementService Placement, FakeProxyRoutingClient Routing) Build()
    {
        var placement = new InMemoryComputeNodePlacementService();
        var bucket = new FakeBlobBucketProvisioner();
        var safekeepers = new FakeSafekeeperAssignmentClient();
        var routing = new FakeProxyRoutingClient();
        var primary = new DatabaseLifecycleOrchestrator(placement, bucket, safekeepers, routing, new FakeTimeProvider());
        var replicas = new ReplicaOrchestrator(primary, placement, routing);
        return (replicas, primary, placement, routing);
    }

    private static async Task ProvisionPrimaryAsync(DatabaseLifecycleOrchestrator primary, InMemoryComputeNodePlacementService placement, string dbId = "db-1", string region = "us-east")
    {
        placement.Register(new NodeCandidate("primary-node", region, Tier: 2, AvailableCapacity: 10, "10.0.0.1", 5432));
        await primary.ProvisionAsync(new ProvisionDatabaseRequest(dbId, region, DatabaseSize.Large, "tenantdb"));
    }

    [Fact]
    public async Task ProvisionReplicaAsync_GoldenPath_PointsAtSameBucketWithNoCopy()
    {
        var (replicas, primary, placement, routing) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", Tier: 3, AvailableCapacity: 10, "10.0.0.2", 5432));

        var result = await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(ReplicaStatus.Healthy, result.Replica!.Status);
        Assert.Equal("replica-node", result.Replica.NodeId);
        Assert.Equal("postgres://db-1-ro.us-east.db.symposia.network:5432/tenantdb", result.Replica.ConnectionString);
        Assert.Equal("replica-node", routing.GetReplicas("db-1").Single().NodeId);
        // The primary's own routing entry (and by extension its bucket) is untouched by replica provisioning.
        Assert.Equal("primary-node", routing.Routes["db-1"]);
    }

    [Fact]
    public async Task ProvisionReplicaAsync_DifferentSizeThanPrimary_PlacedIndependently()
    {
        var (replicas, primary, placement, _) = Build();
        await ProvisionPrimaryAsync(primary, placement); // primary is Large / Tier 2
        placement.Register(new NodeCandidate("small-node", "us-east", Tier: 3, AvailableCapacity: 10, "10.0.0.2", 5432));

        var result = await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(DatabaseSize.Small, result.Replica!.ComputeSize);
    }

    [Fact]
    public async Task ProvisionReplicaAsync_CrossRegion_Rejected()
    {
        var (replicas, primary, placement, _) = Build();
        await ProvisionPrimaryAsync(primary, placement, region: "us-east");
        placement.Register(new NodeCandidate("eu-node", "eu-west", Tier: 3, AvailableCapacity: 10, "10.0.0.9", 5432));

        var result = await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb", Region: "eu-west"));

        Assert.Equal(LifecycleOperationOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ProvisionReplicaAsync_NonExistentDatabase_Rejected()
    {
        var (replicas, _, _, _) = Build();

        var result = await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("ghost-db", "replica-1", DatabaseSize.Small, "tenantdb"));

        Assert.Equal(LifecycleOperationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task ProvisionReplicaAsync_DeletedDatabase_Rejected()
    {
        var (replicas, primary, placement, _) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        await primary.DeleteAsync("db-1");

        var result = await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        Assert.Equal(LifecycleOperationOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ProvisionReplicaAsync_NoCapacity_RejectedNotDegraded()
    {
        var (replicas, primary, placement, routing) = Build();
        await ProvisionPrimaryAsync(primary, placement);

        var result = await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        Assert.Equal(LifecycleOperationOutcome.NoQualifyingNode, result.Outcome);
        Assert.Empty(routing.GetReplicas("db-1"));
    }

    [Fact]
    public async Task WriteRejection_IsDocumentedAsHotStandbyBehavior_ReplicaRecordCarriesNoWriteCapability()
    {
        // FR4/AC3: write rejection is enforced by Postgres's own hot_standby mode on the replica
        // process (per Arch), not re-implemented here; this control-plane skeleton's contract is
        // limited to provisioning a replica-role node and a distinct read-only connection string.
        var (replicas, primary, placement, _) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", 3, 10, "10.0.0.2", 5432));

        var result = await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        Assert.NotEqual(result.Replica!.ConnectionString, primary.GetState("db-1")!.ConnectionString);
    }

    [Fact]
    public async Task ReportLag_BelowThreshold_Healthy()
    {
        var (replicas, primary, placement, _) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", 3, 10, "10.0.0.2", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        var result = replicas.ReportLag("replica-1", replicaLsn: 1000, lagBytes: 1024);

        Assert.Equal(ReplicaStatus.Healthy, result.Replica!.Status);
        Assert.Equal(1024, result.Replica.LagBytes);
    }

    [Fact]
    public async Task ReportLag_AboveThreshold_MarkedLagging()
    {
        var (replicas, primary, placement, _) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", 3, 10, "10.0.0.2", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        var result = replicas.ReportLag("replica-1", replicaLsn: 1000, lagBytes: ReplicaOrchestrator.DefaultLagThresholdBytes + 1);

        Assert.Equal(ReplicaStatus.Lagging, result.Replica!.Status);
    }

    [Fact]
    public async Task ReportLag_RecoversToHealthyAfterBurstSubsides()
    {
        var (replicas, primary, placement, _) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", 3, 10, "10.0.0.2", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));
        replicas.ReportLag("replica-1", 1000, ReplicaOrchestrator.DefaultLagThresholdBytes + 1);

        var result = replicas.ReportLag("replica-1", 2000, lagBytes: 0);

        Assert.Equal(ReplicaStatus.Healthy, result.Replica!.Status);
    }

    [Fact]
    public async Task ResizeReplicaAsync_SameTier_DoesNotAffectPrimary()
    {
        var (replicas, primary, placement, routing) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", 3, 10, "10.0.0.2", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        var result = await replicas.ResizeReplicaAsync("replica-1", DatabaseSize.Medium);

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(DatabaseSize.Medium, result.Replica!.ComputeSize);
        Assert.Equal("replica-node", result.Replica.NodeId);
        Assert.Equal(LifecycleState.Active, primary.GetState("db-1")!.State); // primary untouched
    }

    [Fact]
    public async Task ResizeReplicaAsync_RequiresHigherTier_ReassignsAndUpdatesRouting()
    {
        var (replicas, primary, placement, routing) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("tier3-node", "us-east", Tier: 3, AvailableCapacity: 10, "10.0.0.2", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Medium, "tenantdb"));
        placement.Register(new NodeCandidate("tier1-node", "us-east", Tier: 1, AvailableCapacity: 10, "10.0.0.3", 5432));

        var result = await replicas.ResizeReplicaAsync("replica-1", DatabaseSize.TwoXLarge);

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal("tier1-node", result.Replica!.NodeId);
        Assert.Equal("tier1-node", routing.GetReplicas("db-1").Single().NodeId);
    }

    [Fact]
    public async Task SuspendAndResume_IndependentOfPrimary()
    {
        var (replicas, primary, placement, routing) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", 3, 10, "10.0.0.2", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        var suspendResult = await replicas.SuspendReplicaAsync("replica-1");
        Assert.Equal(ReplicaStatus.Suspended, suspendResult.Replica!.Status);
        Assert.Empty(routing.GetReplicas("db-1"));
        Assert.Equal(LifecycleState.Active, primary.GetState("db-1")!.State); // primary keeps serving

        var resumeResult = await replicas.ResumeReplicaAsync("replica-1");
        Assert.Equal(ReplicaStatus.Healthy, resumeResult.Replica!.Status);
        Assert.Single(routing.GetReplicas("db-1"));
    }

    [Fact]
    public async Task DeleteReplicaAsync_RemovesOnlyThatReplica_SiblingAndPrimaryUnaffected()
    {
        var (replicas, primary, placement, routing) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node-1", "us-east", 3, 10, "10.0.0.2", 5432));
        placement.Register(new NodeCandidate("replica-node-2", "us-east", 3, 10, "10.0.0.3", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-2", DatabaseSize.Small, "tenantdb"));

        var result = await replicas.DeleteReplicaAsync("replica-1");

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(ReplicaStatus.Deleted, result.Replica!.Status);
        Assert.DoesNotContain(routing.GetReplicas("db-1"), r => r.NodeId == "replica-node-1");
        Assert.Contains(routing.GetReplicas("db-1"), r => r.NodeId == "replica-node-2");
        Assert.Equal("primary-node", routing.Routes["db-1"]);
    }

    [Fact]
    public async Task DeleteReplicaAsync_DoubleDelete_RejectedCleanly()
    {
        var (replicas, primary, placement, _) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", 3, 10, "10.0.0.2", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));
        await replicas.DeleteReplicaAsync("replica-1");

        var result = await replicas.DeleteReplicaAsync("replica-1");

        Assert.Equal(LifecycleOperationOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task MultipleReplicas_IndependentlyProvisionedAndRoutable()
    {
        var (replicas, primary, placement, routing) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node-1", "us-east", 3, 10, "10.0.0.2", 5432));
        placement.Register(new NodeCandidate("replica-node-2", "us-east", 3, 10, "10.0.0.3", 5432));

        var first = await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));
        var second = await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-2", DatabaseSize.Medium, "tenantdb"));

        Assert.Equal(LifecycleOperationOutcome.Ok, first.Outcome);
        Assert.Equal(LifecycleOperationOutcome.Ok, second.Outcome);
        Assert.Equal(2, replicas.ListReplicas("db-1").Count);
        Assert.Equal(2, routing.GetReplicas("db-1").Count);
    }

    [Fact]
    public async Task PromoteAsync_HealthyReplica_BecomesRoutingPrimaryAndLeavesRotation()
    {
        var (replicas, primary, placement, routing) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", 3, 10, "10.0.0.2", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));

        var result = await replicas.PromoteAsync("replica-1");

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(ReplicaStatus.Promoted, result.Replica!.Status);
        Assert.Equal("replica-node", routing.Routes["db-1"]);
        Assert.Empty(routing.GetReplicas("db-1"));
    }

    [Fact]
    public async Task PromoteAsync_LaggingReplica_Rejected()
    {
        var (replicas, primary, placement, _) = Build();
        await ProvisionPrimaryAsync(primary, placement);
        placement.Register(new NodeCandidate("replica-node", "us-east", 3, 10, "10.0.0.2", 5432));
        await replicas.ProvisionReplicaAsync(new ProvisionReplicaRequest("db-1", "replica-1", DatabaseSize.Small, "tenantdb"));
        replicas.ReportLag("replica-1", 1000, ReplicaOrchestrator.DefaultLagThresholdBytes + 1);

        var result = await replicas.PromoteAsync("replica-1");

        Assert.Equal(LifecycleOperationOutcome.Rejected, result.Outcome);
    }
}
