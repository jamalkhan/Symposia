using Symposia.Database.ComputeNode;
using Symposia.Database.ComputeNode.Archival;
using Symposia.Database.ComputeNode.Benchmark;
using Symposia.Database.ComputeNode.Databases;
using Symposia.Database.ComputeNode.Identity;
using Symposia.Database.ComputeNode.Lifecycle;
using Symposia.Database.ComputeNode.Proxy;
using Symposia.Database.ComputeNode.Safekeeping;
using Symposia.Database.ComputeNode.Supervision;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ComputeNodeOptions>(builder.Configuration.GetSection("ComputeNode"));
builder.Services.AddSingleton<NodeIdentity>();
builder.Services.AddSingleton<IProcessLauncher, OsProcessLauncher>();
builder.Services.AddSingleton<ComputeNodeSupervisor>();
builder.Services.AddSingleton<IHostInfoProbe, OsHostInfoProbe>();
builder.Services.AddSingleton<IWorkloadSampler, DefaultWorkloadSampler>();
builder.Services.AddSingleton<SustainedBenchmarkRunner>();
builder.Services.AddSingleton<SafekeeperCoordinationService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IBlobUploader>(sp =>
{
    var supervisor = sp.GetRequiredService<ComputeNodeSupervisor>();
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpBlobUploader));
    return new HttpBlobUploader(httpClient, tenantDatabaseId =>
    {
        var database = supervisor.GetDatabase(tenantDatabaseId)
            ?? throw new InvalidOperationException($"No hosted database '{tenantDatabaseId}' to resolve an archival destination for.");
        return new WalArchivalDestination(
            database.BlobBucketUrl ?? throw new InvalidOperationException($"Database '{tenantDatabaseId}' has no BlobBucketUrl configured."),
            database.BlobBucketCredential ?? throw new InvalidOperationException($"Database '{tenantDatabaseId}' has no BlobBucketCredential configured."));
    });
});
builder.Services.AddSingleton<WalArchiver>();

// DB proxy connection pooling/routing/auth control-plane skeleton (issue #93). The proxy binary
// itself is a separate forked-pooler process per the #93 architectural plan; these services are
// the shared routing/auth/admission/wake-on-connect logic a proxy sidecar would consume, exposed
// here as internal control-plane endpoints for now.
builder.Services.AddSingleton<RoutingTableService>();
builder.Services.AddSingleton<AuthCacheService>();
builder.Services.AddSingleton<ConnectionAdmissionService>();
builder.Services.AddHttpClient<ILifecycleClient, HttpLifecycleClient>();
builder.Services.AddSingleton<WakeOnConnectCoordinator>();
builder.Services.AddSingleton<ConnectionRouter>();

// Database provisioning/scaling/suspend-resume/deletion lifecycle orchestrator (issue #95).
// Single writer of lifecycle state per db_id; consumes #90-style capacity data via
// IComputeNodePlacementService, #94's safekeeper coordination, and #93's routing table.
builder.Services.AddSingleton<IComputeNodePlacementService, InMemoryComputeNodePlacementService>();
builder.Services.AddSingleton<ISafekeeperAssignmentClient, SafekeeperAssignmentClient>();
builder.Services.AddSingleton<IProxyRoutingClient, ProxyRoutingClient>();
builder.Services.AddHttpClient<IBlobBucketProvisioner, HttpBlobBucketProvisioner>();
builder.Services.AddSingleton<DatabaseLifecycleOrchestrator>();

var app = builder.Build();

app.Services.GetRequiredService<NodeIdentity>().EnsureLoadedOrGenerated();
app.Services.GetRequiredService<ComputeNodeSupervisor>().Start();

// Internal plumbing (Postgres <-> pageserver, Postgres <-> safekeeper) stays on loopback/unix
// sockets and never traverses this listener. This HTTP surface is the local, node-scoped
// control API from the architectural plan on issue #88 -- reachable by orchestration only,
// never the public wire-protocol/safekeeper listeners (those are a separate, mTLS-gated
// concern layered on top, out of scope for this issue's daemon skeleton).

app.MapGet("/healthz", (ComputeNodeSupervisor supervisor) =>
    supervisor.IsLive
        ? Results.Ok(new { status = supervisor.IsHealthy ? "healthy" : "degraded", components = supervisor.ComponentStates })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet("/readyz", (ComputeNodeSupervisor supervisor) =>
{
    var ready = supervisor.CanAcceptNewPlacements;
    return ready
        ? Results.Ok(new { ready = true })
        : Results.Json(new { ready = false, reason = "unhealthy-or-at-capacity" }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/databases", (ComputeNodeSupervisor supervisor) =>
    Results.Ok(supervisor.ListDatabases()));

app.MapPost("/databases", (PlaceDatabaseRequest request, ComputeNodeSupervisor supervisor) =>
{
    var result = supervisor.PlaceDatabase(request);
    return result.Outcome switch
    {
        PlaceDatabaseOutcome.Placed => Results.Created($"/databases/{result.Database!.TenantDatabaseId}", result.Database),
        PlaceDatabaseOutcome.Conflict => Results.Conflict(new { error = result.Reason }),
        PlaceDatabaseOutcome.UnsupportedVersion => Results.BadRequest(new { error = result.Reason }),
        PlaceDatabaseOutcome.CapacityExceeded => Results.Json(new { error = result.Reason }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Problem(),
    };
});

app.MapDelete("/databases/{tenantDatabaseId}", (string tenantDatabaseId, ComputeNodeSupervisor supervisor) =>
    supervisor.RemoveDatabase(tenantDatabaseId) ? Results.NoContent() : Results.NotFound());

// Benchmark suite (issue #89). Like the rest of this control API, restricting these to an
// authenticated witness identity is mTLS-gating work carried as a follow-on alongside #88's own
// deferred public-listener auth -- this loopback/orchestration-only surface does not yet enforce
// caller identity. The daemon only executes and reports; classification happens on the
// witness/registry side (ComputeTierRegistry), never here.
app.MapGet("/hostinfo", (IHostInfoProbe probe) => Results.Ok(probe.Probe()));

app.MapPost("/benchmark/run", async (SustainedBenchmarkRunner runner, CancellationToken cancellationToken) =>
    Results.Ok(await runner.RunAsync(cancellationToken)));

// WAL safekeeper quorum coordination and archival (issue #94). Internal, control-plane-only
// surfaces per the architectural plan -- not tenant-facing. #95's provisioning flow calls the
// safekeeper assignment endpoints; the /safekeeper/timelines endpoints are this node's own
// self-reporting surface (RTT breach, archival watermark) consumed by the coordination service.
app.MapPost("/internal/databases/{dbId}/safekeepers", (string dbId, AssignSafekeepersRequest request, SafekeeperCoordinationService coordination) =>
{
    var result = coordination.AssignInitialPeers(dbId, request.PrimaryNodeId, request.Region, request.Candidates);
    return result.Outcome == AssignSafekeepersOutcome.Assigned
        ? Results.Created($"/internal/databases/{dbId}/safekeepers", result.Assignment)
        : Results.Json(new { error = result.Reason }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/internal/databases/{dbId}/safekeepers", (string dbId, SafekeeperCoordinationService coordination) =>
{
    var assignment = coordination.GetAssignment(dbId);
    return assignment is not null ? Results.Ok(assignment) : Results.NotFound();
});

app.MapPost("/internal/databases/{dbId}/safekeepers/reassign", (string dbId, ReassignSafekeeperRequest request, SafekeeperCoordinationService coordination) =>
{
    var result = coordination.ReassignPeer(dbId, request.DegradedPeerNodeId, request.Candidates);
    return result.Outcome == AssignSafekeepersOutcome.Assigned
        ? Results.Ok(result.Assignment)
        : Results.Json(new { error = result.Reason, assignment = result.Assignment }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/internal/databases/{dbId}/wal-archival-status", (string dbId, WalArchiver archiver) =>
    Results.Ok(new
    {
        archivedLsn = archiver.GetArchivedLsn(dbId),
        backlogBytes = archiver.GetBacklogBytes(dbId),
        escalated = archiver.IsEscalated(dbId),
    }));

app.MapPost("/safekeeper/timelines/{timelineId}/rtt-breach", (string timelineId, RttBreachReport report, SafekeeperCoordinationService coordination) =>
{
    var result = coordination.ReassignPeer(report.DatabaseId, report.DegradedPeerNodeId, report.Candidates);
    return result.Outcome == AssignSafekeepersOutcome.Assigned
        ? Results.Ok(result.Assignment)
        : Results.Json(new { error = result.Reason, assignment = result.Assignment }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

// DB proxy connection routing (issue #93). Internal, control-plane-only surfaces: the routing
// table is populated by provisioning (#95) and migration (#92) events, and the connect endpoint
// is what a proxy sidecar calls per incoming client connection to get an auth+routing decision.
app.MapPost("/internal/proxy/databases/{dbId}/routing", (string dbId, UpsertRoutingRequest request, RoutingTableService routingTable) =>
    Results.Ok(routingTable.Upsert(dbId, request.Primary, request.Replicas, request.MaxConnections)));

app.MapGet("/internal/proxy/databases/{dbId}/routing", (string dbId, RoutingTableService routingTable) =>
{
    var entry = routingTable.Get(dbId);
    return entry is not null ? Results.Ok(entry) : Results.NotFound();
});

app.MapPost("/internal/proxy/databases/{dbId}/connect", async (string dbId, ConnectRequest request, ConnectionRouter router) =>
{
    var decision = await router.RouteAsync(new Symposia.Database.ComputeNode.Proxy.ConnectionRequest(dbId, request.Username, request.SecretHash, request.ReadOnly));
    return decision.Outcome switch
    {
        ConnectionOutcome.Routed => Results.Ok(decision.Endpoint),
        ConnectionOutcome.AuthRejected => Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status401Unauthorized),
        ConnectionOutcome.ConnectionLimitExceeded => Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status404NotFound),
    };
});

// Database lifecycle API (issue #95): tenant-facing provisioning/resize/suspend-resume/deletion.
app.MapPost("/databases/lifecycle", async (ProvisionDatabaseRequest request, DatabaseLifecycleOrchestrator orchestrator, CancellationToken cancellationToken) =>
{
    var result = await orchestrator.ProvisionAsync(request, cancellationToken);
    return result.Outcome switch
    {
        LifecycleOperationOutcome.Ok => Results.Created($"/databases/lifecycle/{request.DatabaseId}", result.Record),
        LifecycleOperationOutcome.NoQualifyingNode => Results.Json(new { error = result.Reason }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Conflict(new { error = result.Reason }),
    };
});

app.MapGet("/databases/lifecycle/{dbId}", (string dbId, DatabaseLifecycleOrchestrator orchestrator) =>
{
    var record = orchestrator.GetState(dbId);
    return record is not null ? Results.Ok(record) : Results.NotFound();
});

app.MapPatch("/databases/lifecycle/{dbId}/resize", async (string dbId, ResizeRequest request, DatabaseLifecycleOrchestrator orchestrator, CancellationToken cancellationToken) =>
{
    var result = await orchestrator.ResizeAsync(dbId, request.ComputeSize, cancellationToken);
    return result.Outcome switch
    {
        LifecycleOperationOutcome.Ok => Results.Ok(result.Record),
        LifecycleOperationOutcome.NotFound => Results.NotFound(),
        LifecycleOperationOutcome.NoQualifyingNode => Results.Json(new { error = result.Reason }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new { error = result.Reason }, statusCode: StatusCodes.Status409Conflict),
    };
});

app.MapPost("/internal/databases/lifecycle/{dbId}/resume", async (string dbId, DatabaseLifecycleOrchestrator orchestrator, CancellationToken cancellationToken) =>
{
    var result = await orchestrator.ResumeAsync(dbId, cancellationToken);
    return result.Outcome switch
    {
        LifecycleOperationOutcome.Ok => Results.Ok(new Symposia.Database.ComputeNode.Proxy.ComputeEndpoint(result.Record!.PrimaryNodeId!, "", 5432)),
        LifecycleOperationOutcome.NotFound => Results.NotFound(),
        LifecycleOperationOutcome.NoQualifyingNode => Results.Json(new { error = result.Reason }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new { error = result.Reason }, statusCode: StatusCodes.Status409Conflict),
    };
});

app.MapDelete("/databases/lifecycle/{dbId}", async (string dbId, DatabaseLifecycleOrchestrator orchestrator, CancellationToken cancellationToken) =>
{
    var result = await orchestrator.DeleteAsync(dbId, cancellationToken);
    return result.Outcome switch
    {
        LifecycleOperationOutcome.Ok => Results.Ok(result.Record),
        LifecycleOperationOutcome.NotFound => Results.NotFound(),
        _ => Results.Json(new { error = result.Reason }, statusCode: StatusCodes.Status409Conflict),
    };
});

app.Run();

namespace Symposia.Database.ComputeNode
{
    public sealed record UpsertRoutingRequest(Symposia.Database.ComputeNode.Proxy.ComputeEndpoint Primary, IReadOnlyList<Symposia.Database.ComputeNode.Proxy.ComputeEndpoint>? Replicas, int MaxConnections = 200);
    public sealed record ConnectRequest(string Username, string SecretHash, bool ReadOnly = false);
    public sealed record ResizeRequest(Symposia.Database.ComputeNode.Lifecycle.DatabaseSize ComputeSize);
}

namespace Symposia.Database.ComputeNode
{
    /// <summary>Entry point marker for WebApplicationFactory-based integration tests.</summary>
    public partial class Program;
}
