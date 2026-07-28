using Symposia.Database.ComputeNode.Lifecycle;

namespace Symposia.Database.ComputeNode.Tests.Lifecycle;

/// <summary>
/// Traces to #102's FR2 (Major-Version Upgrade Mechanism), AC3-AC5, the Gherkin upgrade/rollback
/// scenarios, and the Arch plan's Compute Attachment Swap primitive + concurrency rule.
/// </summary>
public sealed class ComputeAttachmentSwapOrchestratorTests
{
    private static (
        DatabaseLifecycleOrchestrator Lifecycle,
        ComputeAttachmentSwapOrchestrator Swaps,
        InMemoryComputeNodePlacementService Placement,
        FakeSafekeeperAssignmentClient Safekeepers,
        FakeProxyRoutingClient Routing,
        FakeTimeProvider Time) Build(TimeSpan? graceWindow = null)
    {
        var catalog = new InMemoryPostgresVersionCatalog([new(17, DateTimeOffset.UtcNow.AddYears(3)), new(16, DateTimeOffset.UtcNow.AddYears(2)), new(15, DateTimeOffset.UtcNow.AddYears(1))]);
        var placement = new InMemoryComputeNodePlacementService();
        var bucket = new FakeBlobBucketProvisioner();
        var safekeepers = new FakeSafekeeperAssignmentClient();
        var routing = new FakeProxyRoutingClient();
        var time = new FakeTimeProvider();
        var lifecycle = new DatabaseLifecycleOrchestrator(placement, bucket, safekeepers, routing, time, catalog);
        var swaps = new ComputeAttachmentSwapOrchestrator(lifecycle, placement, safekeepers, routing, catalog, time, graceWindow);
        return (lifecycle, swaps, placement, safekeepers, routing, time);
    }

    private static async Task<string> ProvisionActiveDatabaseAsync(DatabaseLifecycleOrchestrator lifecycle, string dbId = "db-1", int major = 15)
    {
        var result = await lifecycle.ProvisionAsync(new ProvisionDatabaseRequest(dbId, "us-east", DatabaseSize.Medium, "tenantdb", PostgresMajorVersion: major));
        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        return result.Record!.PrimaryNodeId!;
    }

    [Fact]
    public async Task UpgradeAsync_GoldenPath_NewNodeAttachedAndProxyCutOverWithoutBucketMigration()
    {
        var (lifecycle, swaps, placement, safekeepers, routing, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        var oldNodeId = await ProvisionActiveDatabaseAsync(lifecycle);
        var callsBefore = safekeepers.AssignCallCount;
        placement.Register(new NodeCandidate("node-new", "us-east", 2, 10, "10.0.0.2", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));

        var result = await swaps.UpgradeAsync("db-1", targetMajor: 16);

        Assert.Equal(SwapOperationOutcome.Accepted, result.Outcome);
        Assert.Equal(SwapStatus.AwaitingRollbackWindow, result.Swap!.Status);
        Assert.Equal(oldNodeId, result.Swap.OldPrimaryNodeId);
        Assert.Equal("node-new", result.Swap.NewPrimaryNodeId);
        Assert.Equal(15, result.Swap.FromMajor);
        Assert.Equal(16, result.Swap.ToMajor);
        Assert.Equal("node-new", routing.Routes["db-1"]);
        Assert.Equal(16, lifecycle.GetState("db-1")!.PostgresMajorVersion);
        Assert.Equal("node-new", lifecycle.GetState("db-1")!.PrimaryNodeId);
        Assert.True(safekeepers.AssignCallCount > callsBefore); // fresh quorum re-formed on the new node, per Arch step 4
    }

    [Fact]
    public async Task UpgradeAsync_TargetUnsupportedMajor_RejectedBeforeAnyNodeTouched()
    {
        var (lifecycle, swaps, placement, _, routing, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);

        var result = await swaps.UpgradeAsync("db-1", targetMajor: 99);

        Assert.Equal(SwapOperationOutcome.UnsupportedMajorVersion, result.Outcome);
        Assert.Equal(15, lifecycle.GetState("db-1")!.PostgresMajorVersion); // untouched
        Assert.Equal("node-old", routing.Routes["db-1"]);
    }

    [Fact]
    public async Task UpgradeAsync_NoRegionalCapacityForTargetMajor_RejectedWithDistinguishableError_DatabaseUnchanged()
    {
        // Gherkin: "Upgrade request rejected — target major unsupported in region"
        var (lifecycle, swaps, placement, _, routing, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);
        // Target major 17 is platform-supported but no node in-region declares it.

        var result = await swaps.UpgradeAsync("db-1", targetMajor: 17);

        Assert.Equal(SwapOperationOutcome.NoRegionalCapacity, result.Outcome);
        Assert.Equal(15, lifecycle.GetState("db-1")!.PostgresMajorVersion);
        Assert.Equal("node-old", routing.Routes["db-1"]);
    }

    [Fact]
    public async Task UpgradeAsync_CapacityExistsOnlyInAnotherRegion_StillRejected()
    {
        var (lifecycle, swaps, placement, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);
        placement.Register(new NodeCandidate("node-eu", "eu-west", 2, 10, "10.0.1.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));

        var result = await swaps.UpgradeAsync("db-1", targetMajor: 16);

        Assert.Equal(SwapOperationOutcome.NoRegionalCapacity, result.Outcome);
    }

    [Fact]
    public async Task UpgradeAsync_SecondRequestWhileFirstInGraceWindow_RejectedWithConflict()
    {
        var (lifecycle, swaps, placement, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);
        placement.Register(new NodeCandidate("node-16", "us-east", 2, 10, "10.0.0.2", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));
        placement.Register(new NodeCandidate("node-17", "us-east", 2, 10, "10.0.0.3", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 17 }));

        var first = await swaps.UpgradeAsync("db-1", targetMajor: 16);
        var second = await swaps.UpgradeAsync("db-1", targetMajor: 17);

        Assert.Equal(SwapOperationOutcome.Accepted, first.Outcome);
        Assert.Equal(SwapOperationOutcome.Conflict, second.Outcome);
        Assert.Equal(first.Swap!.SwapId, second.Swap!.SwapId);
    }

    [Fact]
    public async Task UpgradeAsync_AfterPriorSwapGraceWindowElapses_NewRequestAllowed()
    {
        var (lifecycle, swaps, placement, _, _, time) = Build(TimeSpan.FromHours(24));
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);
        placement.Register(new NodeCandidate("node-16", "us-east", 2, 10, "10.0.0.2", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));
        placement.Register(new NodeCandidate("node-17", "us-east", 2, 10, "10.0.0.3", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 17 }));
        await swaps.UpgradeAsync("db-1", targetMajor: 16);

        time.Advance(TimeSpan.FromHours(25));
        var second = await swaps.UpgradeAsync("db-1", targetMajor: 17);

        Assert.Equal(SwapOperationOutcome.Accepted, second.Outcome);
    }

    [Fact]
    public async Task RollbackAsync_WithinGraceWindow_ReattachesOldNodeWithNoDataLoss()
    {
        // Gherkin: "Failed upgrade rolls back within the grace window"
        var (lifecycle, swaps, placement, _, routing, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        var oldNodeId = await ProvisionActiveDatabaseAsync(lifecycle);
        placement.Register(new NodeCandidate("node-new", "us-east", 2, 10, "10.0.0.2", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));
        var upgrade = await swaps.UpgradeAsync("db-1", targetMajor: 16);

        var rollback = await swaps.RollbackAsync("db-1", upgrade.Swap!.SwapId);

        Assert.Equal(SwapOperationOutcome.RolledBack, rollback.Outcome);
        Assert.Equal(SwapStatus.RolledBack, rollback.Swap!.Status);
        Assert.Equal(15, lifecycle.GetState("db-1")!.PostgresMajorVersion);
        Assert.Equal(oldNodeId, lifecycle.GetState("db-1")!.PrimaryNodeId);
        Assert.Equal(oldNodeId, routing.Routes["db-1"]);
    }

    [Fact]
    public async Task RollbackAsync_ClearsConcurrencySlot_AllowingANewUpgrade()
    {
        var (lifecycle, swaps, placement, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);
        placement.Register(new NodeCandidate("node-16", "us-east", 2, 10, "10.0.0.2", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));
        placement.Register(new NodeCandidate("node-17", "us-east", 2, 10, "10.0.0.3", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 17 }));
        var upgrade = await swaps.UpgradeAsync("db-1", targetMajor: 16);
        await swaps.RollbackAsync("db-1", upgrade.Swap!.SwapId);

        var second = await swaps.UpgradeAsync("db-1", targetMajor: 17);

        Assert.Equal(SwapOperationOutcome.Accepted, second.Outcome);
    }

    [Fact]
    public async Task RollbackAsync_AfterGraceWindowElapsed_Rejected()
    {
        var (lifecycle, swaps, placement, _, _, time) = Build(TimeSpan.FromHours(24));
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);
        placement.Register(new NodeCandidate("node-new", "us-east", 2, 10, "10.0.0.2", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));
        var upgrade = await swaps.UpgradeAsync("db-1", targetMajor: 16);

        time.Advance(TimeSpan.FromHours(25));
        var rollback = await swaps.RollbackAsync("db-1", upgrade.Swap!.SwapId);

        Assert.Equal(SwapOperationOutcome.RollbackWindowExpired, rollback.Outcome);
        Assert.Equal(16, lifecycle.GetState("db-1")!.PostgresMajorVersion); // stays on the new major
    }

    [Fact]
    public async Task RollbackAsync_UnknownSwapId_NotFound()
    {
        var (lifecycle, swaps, placement, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);

        var result = await swaps.RollbackAsync("db-1", "nonexistent-swap-id");

        Assert.Equal(SwapOperationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task UpgradeAsync_DatabaseNotFound_ReturnsNotFound()
    {
        var (_, swaps, _, _, _, _) = Build();

        var result = await swaps.UpgradeAsync("no-such-db", targetMajor: 16);

        Assert.Equal(SwapOperationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetAuditRecords_RecordsTenantInitiatedTrigger()
    {
        // FR3.3/AC7: audit record captures trigger reason -- tenant-initiated here.
        var (lifecycle, swaps, placement, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);
        placement.Register(new NodeCandidate("node-new", "us-east", 2, 10, "10.0.0.2", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));

        await swaps.UpgradeAsync("db-1", targetMajor: 16);
        var audit = swaps.GetAuditRecords("db-1");

        Assert.Single(audit);
        Assert.Equal(SwapTrigger.Tenant, audit[0].Trigger);
        Assert.Equal(15, audit[0].FromMajor);
        Assert.Equal(16, audit[0].ToMajor);
        Assert.Equal("db-1", audit[0].DatabaseId);
        Assert.NotNull(audit[0].CutoverCompletedAt);
    }

    [Fact]
    public async Task GetAuditRecords_EolEnforcedTriggerIsDistinguishableFromTenant()
    {
        // Schema-only support per #102's "out of scope" note: no sweep job calls this yet, but the
        // trigger field must already support it without a future schema change.
        var (lifecycle, swaps, placement, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);
        placement.Register(new NodeCandidate("node-new", "us-east", 2, 10, "10.0.0.2", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));

        var result = await swaps.UpgradeAsync("db-1", targetMajor: 16, trigger: SwapTrigger.EolEnforced);

        Assert.Equal(SwapOperationOutcome.Accepted, result.Outcome);
        Assert.Equal(SwapTrigger.EolEnforced, swaps.GetAuditRecords("db-1")[0].Trigger);
    }

    [Fact]
    public async Task UpgradeAsync_TargetSameAsCurrentMajor_Rejected()
    {
        var (lifecycle, swaps, placement, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-old", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 15 }));
        await ProvisionActiveDatabaseAsync(lifecycle);

        var result = await swaps.UpgradeAsync("db-1", targetMajor: 15);

        Assert.Equal(SwapOperationOutcome.UnsupportedMajorVersion, result.Outcome);
    }
}
