using Symposia.Database.ComputeNode;
using Symposia.Database.ComputeNode.Databases;
using Symposia.Database.ComputeNode.Identity;
using Symposia.Database.ComputeNode.Supervision;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ComputeNodeOptions>(builder.Configuration.GetSection("ComputeNode"));
builder.Services.AddSingleton<NodeIdentity>();
builder.Services.AddSingleton<IProcessLauncher, OsProcessLauncher>();
builder.Services.AddSingleton<ComputeNodeSupervisor>();

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

app.Run();

namespace Symposia.Database.ComputeNode
{
    /// <summary>Entry point marker for WebApplicationFactory-based integration tests.</summary>
    public partial class Program;
}
