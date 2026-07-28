using Symposia.Identity.Gateway;
using Symposia.Identity.Gateway.Chain;
using Symposia.Identity.Gateway.Endpoints;
using Symposia.Identity.Gateway.Siwe;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Identity"));

// ── Services ─────────────────────────────────────────────────────────────────

builder.Services.AddSingleton<SiweChallengeService>();
builder.Services.AddSingleton<IIdentityBindingStore, InMemoryIdentityBindingStore>();
builder.Services.AddSingleton<IChainClient, EthereumChainClient>();

var app = builder.Build();

// ── Health endpoints ──────────────────────────────────────────────────────────

app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }));

// ── Identity / consent / capability routes ────────────────────────────────────

IdentityEndpoints.Map(app);
ConsentEndpoints.Map(app);

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
