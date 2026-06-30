using Symposia.BlobStorage.Gateway;
using Symposia.BlobStorage.Gateway.Auth;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Quorum;
using Symposia.BlobStorage.Gateway.S3;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────

builder.Services.Configure<GatewayOptions>(
    builder.Configuration.GetSection("Gateway"));

// ── Services ─────────────────────────────────────────────────────────────────

builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<SigV4Verifier>();
builder.Services.AddSingleton<GatewayMetadataStore>();
builder.Services.AddSingleton<NodeRegistry>();
builder.Services.AddSingleton<INodeRegistry>(sp => sp.GetRequiredService<NodeRegistry>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeRegistry>());
builder.Services.AddScoped<QuorumWriter>();

// ── Kestrel: allow h2c (cleartext HTTP/2) for S3 SDK compatibility ────────────
// Production terminates TLS externally (load-balancer or sidecar).
builder.WebHost.ConfigureKestrel(options =>
    options.ListenAnyIP(5181, lo =>
        lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2));

var app = builder.Build();

// ── Init ──────────────────────────────────────────────────────────────────────

app.Services.GetRequiredService<GatewayMetadataStore>().Initialize();

// ── SigV4 middleware ──────────────────────────────────────────────────────────

// Runs before routing. Exempt only health endpoints.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/healthz/", StringComparison.Ordinal))
    {
        await next();
        return;
    }

    var verifier = ctx.RequestServices.GetRequiredService<SigV4Verifier>();
    var tenantId = verifier.Verify(ctx.Request);
    if (tenantId is null)
    {
        ctx.Response.StatusCode = 403;
        ctx.Response.ContentType = "application/xml";
        await ctx.Response.WriteAsync(
            "<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>");
        return;
    }

    ctx.Items["TenantId"] = tenantId;
    await next();
});

// ── Health endpoints ──────────────────────────────────────────────────────────

app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/healthz/ready", (INodeRegistry nodes) =>
{
    var healthy = nodes.Healthy;
    return healthy.Count > 0
        ? Results.Ok(new { status = "ready", nodes = healthy.Count })
        : Results.StatusCode(503);
});

// ── S3 routes ─────────────────────────────────────────────────────────────────

// Object routes must be registered before bucket routes so /{bucket}/{**key}
// takes priority over /{bucket} when a key is present.
ObjectEndpoints.Map(app);
BucketEndpoints.Map(app);

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
