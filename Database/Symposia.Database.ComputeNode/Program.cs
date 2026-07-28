using Symposia.Database.ComputeNode;
using Symposia.Database.ComputeNode.Archival;
using Symposia.Database.ComputeNode.Benchmark;
using Symposia.Database.ComputeNode.Databases;
using Symposia.Database.ComputeNode.Identity;
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

app.Run();

namespace Symposia.Database.ComputeNode
{
    /// <summary>Entry point marker for WebApplicationFactory-based integration tests.</summary>
    public partial class Program;
}
