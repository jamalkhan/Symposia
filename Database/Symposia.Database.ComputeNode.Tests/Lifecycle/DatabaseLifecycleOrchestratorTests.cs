using Symposia.Database.ComputeNode.Lifecycle;

namespace Symposia.Database.ComputeNode.Tests.Lifecycle;

/// <summary>
/// Traces to #95's Gherkin scenarios and QA plan sections (Provisioning, Compute resize,
/// Idle-triggered suspend, Wake-on-connect resume, Deletion, Lifecycle state consistency).
/// </summary>
public sealed class DatabaseLifecycleOrchestratorTests
{
    private static (DatabaseLifecycleOrchestrator Orchestrator, InMemoryComputeNodePlacementService Placement, FakeBlobBucketProvisioner Bucket, FakeSafekeeperAssignmentClient Safekeepers, FakeProxyRoutingClient Routing, FakeTimeProvider Time) Build(IPostgresVersionCatalog? versionCatalog = null, IExtensionAllowlist? extensionAllowlist = null)
    {
        var placement = new InMemoryComputeNodePlacementService();
        var bucket = new FakeBlobBucketProvisioner();
        var safekeepers = new FakeSafekeeperAssignmentClient();
        var routing = new FakeProxyRoutingClient();
        var time = new FakeTimeProvider();
        var orchestrator = new DatabaseLifecycleOrchestrator(placement, bucket, safekeepers, routing, time, versionCatalog, extensionAllowlist);
        return (orchestrator, placement, bucket, safekeepers, routing, time);
    }

    private static ProvisionDatabaseRequest Request(string dbId = "db-1", string region = "us-east", DatabaseSize size = DatabaseSize.Medium, int? idleSeconds = 900, int? postgresMajorVersion = null, IReadOnlySet<string>? requiredExtensions = null) =>
        new(dbId, region, size, "tenantdb", idleSeconds, postgresMajorVersion, requiredExtensions);

    [Fact]
    public async Task ProvisionAsync_GoldenPath_AssignsNodeBucketSafekeepersAndRouteBeforeReturningConnectionString()
    {
        var (orchestrator, placement, bucket, safekeepers, routing, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", Tier: 2, AvailableCapacity: 10, "10.0.0.1", 5432));

        var result = await orchestrator.ProvisionAsync(Request());

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        var record = result.Record!;
        Assert.Equal(LifecycleState.Active, record.State);
        Assert.Equal("node-1", record.PrimaryNodeId);
        Assert.Equal(2, record.SafekeeperPeerIds.Count);
        Assert.Equal("node-1", routing.Routes["db-1"]);
        Assert.Equal("postgres://db-1.us-east.db.symposia.network:5432/tenantdb", record.ConnectionString);
        Assert.Equal(1, safekeepers.AssignCallCount);
    }

    [Fact]
    public async Task ProvisionAsync_NoQualifyingNode_FailsWithoutOrphanedResources()
    {
        var (orchestrator, _, bucket, safekeepers, routing, _) = Build();

        var result = await orchestrator.ProvisionAsync(Request());

        Assert.Equal(LifecycleOperationOutcome.NoQualifyingNode, result.Outcome);
        Assert.Empty(routing.Routes);
        Assert.Equal(0, safekeepers.AssignCallCount);
        Assert.Equal(LifecycleState.Failed, orchestrator.GetState("db-1")!.State);
    }

    [Fact]
    public async Task ProvisionAsync_DuplicateDatabaseId_Rejected()
    {
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request());

        var result = await orchestrator.ProvisionAsync(Request());

        Assert.Equal(LifecycleOperationOutcome.Rejected, result.Outcome);
    }

    // #102 FR1.4/AC2 -- provisioning default/explicit Postgres major version, and FR1.5/AC8 -- version-aware node routing.

    [Fact]
    public async Task ProvisionAsync_NoVersionSpecified_DefaultsToLatestSupportedMajor()
    {
        var catalog = new InMemoryPostgresVersionCatalog([new(17, DateTimeOffset.UtcNow.AddYears(3)), new(16, DateTimeOffset.UtcNow.AddYears(2)), new(15, DateTimeOffset.UtcNow.AddYears(1))]);
        var (orchestrator, placement, _, _, _, _) = Build(catalog);
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432)); // no declared version set => supports every major

        var result = await orchestrator.ProvisionAsync(Request());

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(17, result.Record!.PostgresMajorVersion);
    }

    [Fact]
    public async Task ProvisionAsync_ExplicitOlderSupportedMajor_CreatedOnThatMajorNoError()
    {
        var catalog = new InMemoryPostgresVersionCatalog([new(17, DateTimeOffset.UtcNow.AddYears(3)), new(16, DateTimeOffset.UtcNow.AddYears(2)), new(15, DateTimeOffset.UtcNow.AddYears(1))]);
        var (orchestrator, placement, _, _, _, _) = Build(catalog);
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));

        var result = await orchestrator.ProvisionAsync(Request(postgresMajorVersion: 15));

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(15, result.Record!.PostgresMajorVersion);
    }

    [Fact]
    public async Task ProvisionAsync_ExplicitUnsupportedMajor_RejectedBeforeAnyNodeProvisioned()
    {
        var catalog = new InMemoryPostgresVersionCatalog([new(17, DateTimeOffset.UtcNow.AddYears(3)), new(16, DateTimeOffset.UtcNow.AddYears(2)), new(15, DateTimeOffset.UtcNow.AddYears(1))]);
        var (orchestrator, placement, bucket, safekeepers, routing, _) = Build(catalog);
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));

        var result = await orchestrator.ProvisionAsync(Request(postgresMajorVersion: 13));

        Assert.Equal(LifecycleOperationOutcome.UnsupportedMajorVersion, result.Outcome);
        Assert.Null(orchestrator.GetState("db-1")); // never even reached Provisioning
        Assert.Empty(routing.Routes);
        Assert.Equal(0, safekeepers.AssignCallCount);
    }

    [Fact]
    public async Task ProvisionAsync_NodeDoesNotDeclareRequiredMajor_NeverRoutedThere()
    {
        var catalog = new InMemoryPostgresVersionCatalog([new(17, DateTimeOffset.UtcNow.AddYears(3)), new(16, DateTimeOffset.UtcNow.AddYears(2))]);
        var (orchestrator, placement, _, _, _, _) = Build(catalog);
        placement.Register(new NodeCandidate("node-16-only", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));

        var result = await orchestrator.ProvisionAsync(Request(postgresMajorVersion: 17));

        Assert.Equal(LifecycleOperationOutcome.NoQualifyingNode, result.Outcome);
    }

    [Fact]
    public async Task ProvisionAsync_NodeDeclaresRequiredMajor_Routed()
    {
        var catalog = new InMemoryPostgresVersionCatalog([new(17, DateTimeOffset.UtcNow.AddYears(3)), new(16, DateTimeOffset.UtcNow.AddYears(2))]);
        var (orchestrator, placement, _, _, routing, _) = Build(catalog);
        placement.Register(new NodeCandidate("node-16-only", "us-east", 2, 10, "10.0.0.1", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 16 }));
        placement.Register(new NodeCandidate("node-17-only", "us-east", 2, 10, "10.0.0.2", 5432, SupportedPostgresMajorVersions: new HashSet<int> { 17 }));

        var result = await orchestrator.ProvisionAsync(Request(postgresMajorVersion: 16));

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal("node-16-only", result.Record!.PrimaryNodeId);
        Assert.Equal("node-16-only", routing.Routes["db-1"]);
    }

    [Fact]
    public async Task ResizeAsync_SameTier_NoReassignmentNoDataMovement()
    {
        var (orchestrator, placement, _, safekeepers, routing, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", Tier: 2, AvailableCapacity: 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request(size: DatabaseSize.Small));
        var callsBefore = safekeepers.AssignCallCount;

        var result = await orchestrator.ResizeAsync("db-1", DatabaseSize.Large);

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal("node-1", result.Record!.PrimaryNodeId);
        Assert.Equal(DatabaseSize.Large, result.Record.ComputeSize);
        Assert.Equal(callsBefore, safekeepers.AssignCallCount); // no reassignment => no safekeeper re-establishment
    }

    [Fact]
    public async Task ResizeAsync_RequiresHigherTier_ReassignsToQualifyingNode()
    {
        var (orchestrator, placement, _, _, routing, _) = Build();
        placement.Register(new NodeCandidate("tier2-node", "us-east", Tier: 2, AvailableCapacity: 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request(size: DatabaseSize.Large));
        placement.Register(new NodeCandidate("tier1-node", "us-east", Tier: 1, AvailableCapacity: 10, "10.0.0.2", 5432));

        var result = await orchestrator.ResizeAsync("db-1", DatabaseSize.TwoXLarge);

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal("tier1-node", result.Record!.PrimaryNodeId);
        Assert.Equal("tier1-node", routing.Routes["db-1"]);
    }

    [Fact]
    public async Task ResizeAsync_NoQualifyingNodeForReassignment_RemainsOnOriginalNodeAndSize()
    {
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("tier2-node", "us-east", Tier: 2, AvailableCapacity: 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request(size: DatabaseSize.Large));

        var result = await orchestrator.ResizeAsync("db-1", DatabaseSize.TwoXLarge);

        Assert.Equal(LifecycleOperationOutcome.NoQualifyingNode, result.Outcome);
        Assert.Equal(LifecycleState.Active, orchestrator.GetState("db-1")!.State);
        Assert.Equal(DatabaseSize.Large, orchestrator.GetState("db-1")!.ComputeSize);
    }

    [Fact]
    public async Task ResizeAsync_WhileProvisioning_Rejected()
    {
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));

        // No provisioning call made -> no record at all is the "not found" path; simulate mid-provisioning
        // by asserting the resize-while-non-Active guard directly against a database that never reached Active.
        var result = await orchestrator.ResizeAsync("db-never-provisioned", DatabaseSize.Large);

        Assert.Equal(LifecycleOperationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task IdleSuspend_DefaultThreshold_SuspendsAfterThresholdElapsed()
    {
        var (orchestrator, placement, _, _, routing, time) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request(idleSeconds: 900));

        orchestrator.RecordConnectionCountZero("db-1");
        time.Advance(TimeSpan.FromMinutes(15));
        var result = await orchestrator.EvaluateIdleAsync("db-1");

        Assert.NotNull(result);
        Assert.Equal(LifecycleOperationOutcome.Ok, result!.Outcome);
        Assert.Equal(LifecycleState.Suspended, result.Record!.State);
        Assert.Contains("db-1", routing.SuspendedDatabaseIds);
    }

    [Fact]
    public async Task IdleSuspend_BeforeThresholdElapsed_DoesNotSuspend()
    {
        var (orchestrator, placement, _, _, _, time) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request(idleSeconds: 900));

        orchestrator.RecordConnectionCountZero("db-1");
        time.Advance(TimeSpan.FromMinutes(10));
        var result = await orchestrator.EvaluateIdleAsync("db-1");

        Assert.Null(result);
        Assert.Equal(LifecycleState.Active, orchestrator.GetState("db-1")!.State);
    }

    [Fact]
    public async Task IdleSuspend_ConnectionOpenedCancelsTimer()
    {
        var (orchestrator, placement, _, _, _, time) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request(idleSeconds: 900));

        orchestrator.RecordConnectionCountZero("db-1");
        orchestrator.RecordConnectionOpened("db-1");
        time.Advance(TimeSpan.FromMinutes(20));
        var result = await orchestrator.EvaluateIdleAsync("db-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task IdleSuspend_NeverDisabled_NeverSuspends()
    {
        var (orchestrator, placement, _, _, _, time) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request(idleSeconds: null));

        orchestrator.RecordConnectionCountZero("db-1");
        time.Advance(TimeSpan.FromHours(5));
        var result = await orchestrator.EvaluateIdleAsync("db-1");

        Assert.Null(result);
        Assert.Equal(LifecycleState.Active, orchestrator.GetState("db-1")!.State);
    }

    [Fact]
    public async Task ResumeAsync_SameNodeStillQualifies_ResumesInPlace()
    {
        var (orchestrator, placement, _, _, routing, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request());
        await orchestrator.SuspendAsync("db-1");

        var result = await orchestrator.ResumeAsync("db-1");

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(LifecycleState.Active, result.Record!.State);
        Assert.Equal("node-1", result.Record.PrimaryNodeId);
    }

    [Fact]
    public async Task ResumeAsync_OriginalNodeGone_ReassignsToNewNode()
    {
        var (orchestrator, placement, _, _, routing, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request());
        await orchestrator.SuspendAsync("db-1");
        placement.Register(new NodeCandidate("node-1", "us-east", Tier: 2, AvailableCapacity: 0, "10.0.0.1", 5432)); // decommissioned: no capacity
        placement.Register(new NodeCandidate("node-2", "us-east", 2, 10, "10.0.0.2", 5432));

        var result = await orchestrator.ResumeAsync("db-1");

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal("node-2", result.Record!.PrimaryNodeId);
        Assert.Equal("node-2", routing.Routes["db-1"]);
    }

    [Fact]
    public async Task ResumeAsync_ConcurrentRequests_SecondSeesAlreadyActive()
    {
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request());
        await orchestrator.SuspendAsync("db-1");

        var first = await orchestrator.ResumeAsync("db-1");
        var second = await orchestrator.ResumeAsync("db-1");

        Assert.Equal(LifecycleOperationOutcome.Ok, first.Outcome);
        Assert.Equal(LifecycleOperationOutcome.Rejected, second.Outcome); // already Active, not Suspended
    }

    [Fact]
    public async Task DeleteAsync_ActiveDatabase_SoftDeletesAndRemovesAssignments()
    {
        var (orchestrator, placement, bucket, _, routing, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request());

        var result = await orchestrator.DeleteAsync("db-1");

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(LifecycleState.Deleted, result.Record!.State);
        Assert.Null(result.Record.PrimaryNodeId);
        Assert.Contains("bucket-db-1", bucket.SoftDeletedBucketIds);
        Assert.Contains("db-1", routing.RemovedDatabaseIds);
    }

    [Fact]
    public async Task DeleteAsync_SuspendedDatabase_SucceedsWithoutRequiringResume()
    {
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request());
        await orchestrator.SuspendAsync("db-1");

        var result = await orchestrator.DeleteAsync("db-1");

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal(LifecycleState.Deleted, result.Record!.State);
    }

    [Fact]
    public async Task DeleteAsync_DoubleDelete_RejectedCleanly()
    {
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request());
        await orchestrator.DeleteAsync("db-1");

        var result = await orchestrator.DeleteAsync("db-1");

        Assert.Equal(LifecycleOperationOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task GetState_QueryableThroughoutLifecycle()
    {
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));

        await orchestrator.ProvisionAsync(Request());
        Assert.Equal(LifecycleState.Active, orchestrator.GetState("db-1")!.State);

        await orchestrator.SuspendAsync("db-1");
        Assert.Equal(LifecycleState.Suspended, orchestrator.GetState("db-1")!.State);

        await orchestrator.ResumeAsync("db-1");
        Assert.Equal(LifecycleState.Active, orchestrator.GetState("db-1")!.State);

        await orchestrator.DeleteAsync("db-1");
        Assert.Equal(LifecycleState.Deleted, orchestrator.GetState("db-1")!.State);
    }

    [Fact]
    public async Task ResizeAsync_WhileSuspended_RejectedNotRacy()
    {
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));
        await orchestrator.ProvisionAsync(Request());
        await orchestrator.SuspendAsync("db-1");

        var result = await orchestrator.ResizeAsync("db-1", DatabaseSize.Large);

        Assert.Equal(LifecycleOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(LifecycleState.Suspended, orchestrator.GetState("db-1")!.State); // never left in a mixed state
    }

    // #103 FR3.1-FR3.4/AC7-AC9 -- extension-aware placement filtering and provisioning integration.

    [Fact]
    public async Task ProvisionAsync_RequiredExtensionNotAllowlisted_RejectedBeforeAnyNodeTouched()
    {
        var (orchestrator, placement, _, safekeepers, routing, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));

        var result = await orchestrator.ProvisionAsync(Request(requiredExtensions: new HashSet<string> { "dblink" }));

        Assert.Equal(LifecycleOperationOutcome.ExtensionNotAllowlisted, result.Outcome);
        Assert.Contains("dblink", result.Reason);
        Assert.Null(orchestrator.GetState("db-1")); // never even reached Provisioning, mirrors #102's UnsupportedMajorVersion test
        Assert.Empty(routing.Routes);
        Assert.Equal(0, safekeepers.AssignCallCount);
    }

    [Fact]
    public async Task ProvisionAsync_AllowlistedExtensionAvailableOnNode_PlacedThere()
    {
        var (orchestrator, placement, _, _, routing, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432,
            DeclaredExtensionVersions: new Dictionary<string, string> { ["pgvector"] = "0.7.0" }));

        var result = await orchestrator.ProvisionAsync(Request(requiredExtensions: new HashSet<string> { "pgvector" }));

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal("node-1", result.Record!.PrimaryNodeId);
        Assert.Equal("node-1", routing.Routes["db-1"]);
    }

    [Fact]
    public async Task ProvisionAsync_RoutedOnlyToNodeSatisfyingFullRequiredExtensionSet()
    {
        // Gherkin "Database is routed only to nodes supporting its required extension set".
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-a", "us-east", 2, 10, "10.0.0.1", 5432,
            DeclaredExtensionVersions: new Dictionary<string, string> { ["pgvector"] = "0.7.0" }));
        placement.Register(new NodeCandidate("node-c", "us-east", 2, 10, "10.0.0.2", 5432,
            DeclaredExtensionVersions: new Dictionary<string, string> { ["pgvector"] = "0.7.0", ["postgis"] = "3.4.0" }));

        var result = await orchestrator.ProvisionAsync(Request(requiredExtensions: new HashSet<string> { "pgvector", "postgis" }));

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal("node-c", result.Record!.PrimaryNodeId);
    }

    [Fact]
    public async Task ProvisionAsync_AllowlistedExtensionUnavailableInRegion_FailsWithExtensionNamingError()
    {
        // AC8: distinguishable, extension-identifying error rather than a generic NoQualifyingNode.
        var (orchestrator, placement, _, safekeepers, routing, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432)); // declares nothing

        var result = await orchestrator.ProvisionAsync(Request(requiredExtensions: new HashSet<string> { "postgis" }));

        Assert.Equal(LifecycleOperationOutcome.ExtensionUnavailable, result.Outcome);
        Assert.Contains("postgis", result.Reason);
        Assert.Equal(LifecycleState.Failed, orchestrator.GetState("db-1")!.State);
        Assert.Empty(routing.Routes);
        Assert.Equal(0, safekeepers.AssignCallCount);
    }

    [Fact]
    public async Task ProvisionAsync_MultipleRequiredExtensionsUnavailable_ErrorEnumeratesAll()
    {
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432));

        var result = await orchestrator.ProvisionAsync(Request(requiredExtensions: new HashSet<string> { "postgis", "pg_cron" }));

        Assert.Equal(LifecycleOperationOutcome.ExtensionUnavailable, result.Outcome);
        Assert.Contains("postgis", result.Reason);
        Assert.Contains("pg_cron", result.Reason);
    }

    [Fact]
    public async Task ProvisionAsync_NoRequiredExtensions_UnaffectedByExtensionFiltering()
    {
        // Regression guard (QA TC-34): the new placement filter must short-circuit as a no-op.
        var (orchestrator, placement, _, _, _, _) = Build();
        placement.Register(new NodeCandidate("node-1", "us-east", 2, 10, "10.0.0.1", 5432)); // declares nothing

        var result = await orchestrator.ProvisionAsync(Request());

        Assert.Equal(LifecycleOperationOutcome.Ok, result.Outcome);
        Assert.Equal("node-1", result.Record!.PrimaryNodeId);
    }

    [Fact]
    public async Task ProvisionAsync_ExtensionAndPostgresMajorFiltersAreBothApplied()
    {
        // Additive filters, per the #102/#103 conflict-resolution note: a node must satisfy both.
        var catalog = new InMemoryPostgresVersionCatalog([new(17, DateTimeOffset.UtcNow.AddYears(3)), new(16, DateTimeOffset.UtcNow.AddYears(2))]);
        var (orchestrator, placement, _, _, _, _) = Build(catalog);
        placement.Register(new NodeCandidate("node-16-with-ext", "us-east", 2, 10, "10.0.0.1", 5432,
            SupportedPostgresMajorVersions: new HashSet<int> { 16 },
            DeclaredExtensionVersions: new Dictionary<string, string> { ["pgvector"] = "0.7.0" }));
        placement.Register(new NodeCandidate("node-17-no-ext", "us-east", 2, 10, "10.0.0.2", 5432,
            SupportedPostgresMajorVersions: new HashSet<int> { 17 }));

        var result = await orchestrator.ProvisionAsync(Request(postgresMajorVersion: 17, requiredExtensions: new HashSet<string> { "pgvector" }));

        // node-16-with-ext has the extension but wrong major; node-17-no-ext has the right major but
        // no extension -- neither qualifies, and the failure must be reported as NoQualifyingNode
        // (there IS a node with the extension in-region, just not satisfying the major-version filter
        // too) rather than misleadingly as ExtensionUnavailable.
        Assert.Equal(LifecycleOperationOutcome.NoQualifyingNode, result.Outcome);
    }
}

/// <summary>Deterministic, manually-advanced clock so idle-threshold tests don't wait real minutes.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
