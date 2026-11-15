using Microsoft.Extensions.Options;
using Symposia.BlobStorage.CredentialAuthority;
using Symposia.BlobStorage.Domain.Credentials;

// The Credential Authority (BlobStorage #3): the durable source of truth for tenants, credentials, bucket
// policies, object ACLs and the credential audit log. Gateways cache its state (Watch + poll) and enforce it
// locally; nothing here sits on the data path.
var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<AuthorityOptions>(builder.Configuration.GetSection("CredentialAuthority"));
builder.Services.AddSingleton<AuthorityStore>();
builder.Services.AddHostedService<RevocationPruner>();
builder.Services.AddGrpc();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();
await app.Services.GetRequiredService<AuthorityStore>().MigrateAsync(CancellationToken.None);

app.MapGrpcService<WatchService>();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ── Platform operator: tenant lifecycle (FR2) ─────────────────────────────────

var ops = app.MapGroup("/v1/tenants").AddEndpointFilter(async (ctx, next) =>
    Tokens.Matches(ctx.HttpContext.Request.Headers["X-Operator-Token"].FirstOrDefault(), Opts(ctx.HttpContext).OperatorToken)
        ? await next(ctx) : Results.StatusCode(401));

ops.MapPost("/", async (CreateTenant body, AuthorityStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.TenantId) || string.IsNullOrWhiteSpace(body.AdminIdentity))
        return Results.BadRequest(new { error = "tenantId and adminIdentity are required" });

    var admin = await store.CreateTenantAsync(body.TenantId, body.AdminIdentity, "operator", ct);
    return admin is null ? Results.Conflict(new { error = "tenant exists" }) : Results.Ok(Issued(admin));
});

ops.MapPost("/{tenantId}/{action}", async (string tenantId, string action, AuthorityStore store, CancellationToken ct) =>
{
    TenantStatus? status = action switch
    {
        "suspend" => TenantStatus.Suspended,
        "reactivate" => TenantStatus.Active,
        "delete" => TenantStatus.SoftDeleted,   // recoverable for TenantRecoveryDays
        "purge" => TenantStatus.Purged,         // only once the recovery window has passed
        _ => null,
    };
    if (status is null) return Results.NotFound();
    return await store.SetTenantStatusAsync(tenantId, status.Value, "operator", ct)
        ? Results.Ok(new { tenantId, status = status.Value.ToString() })
        : Results.Conflict(new { error = "transition not allowed now" });
});

// ── Gateways: state and audit, and management on behalf of a tenant credential ───

var internalApi = app.MapGroup("/internal").AddEndpointFilter(async (ctx, next) =>
    Tokens.Matches(ctx.HttpContext.Request.Headers["X-Service-Token"].FirstOrDefault(), Opts(ctx.HttpContext).ServiceToken)
        ? await next(ctx) : Results.StatusCode(401));

// Secret-bearing and per-caller: never cached anywhere (#245).
internalApi.MapGet("/snapshot", async (HttpContext http, AuthorityStore store, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await store.SnapshotAsync(ct));
});

// Deltas since a version (#245 FR2). 410 tells the gateway to take the snapshot instead.
internalApi.MapGet("/changes", async (long? since, HttpContext http, AuthorityStore store, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-store";
    if (since is not >= 0) return Results.BadRequest(new { error = "since must be a non-negative version" });
    var (delta, refusal) = await store.ChangesAsync(since.Value, ct);
    return delta is not null ? Results.Ok(delta) : Results.Json(new { error = "SnapshotRequired", reason = refusal.ToString() }, statusCode: 410);
});

internalApi.MapPost("/audit", async (UseBatch batch, AuthorityStore store, CancellationToken ct) =>
{
    // 204 whether new or a replay: either way the gateway may drop its copy.
    await store.RecordUsesAsync(batch, ct);
    return Results.NoContent();
});

internalApi.MapPost("/credentials", async (IssueRequest body, HttpContext http, AuthorityStore store, CancellationToken ct) =>
{
    if (await ActingAsync(http, store, ct) is not { } acting) return Results.StatusCode(403);
    var scope = new ResourceScope(body.Bucket, body.Prefix, body.Key);
    var level = body.AccessLevel ?? AccessLevel.Read;
    // Minimum necessary access (FR3.10): never broader than the issuer in scope, level, lifetime or regions,
    // and only a manager may mint another manager.
    if (!acting.CanManage || !scope.IsWithin(acting.Scope) || level > acting.AccessLevel || (body.CanManage && !acting.CanManage)
        || (acting.ExpiresAt is { } cap && (body.ExpiresAt is null || body.ExpiresAt > cap))
        || (acting.RegionLock.Count > 0 && (body.RegionLock is not { Length: > 0 } || body.RegionLock.Except(acting.RegionLock).Any())))
        return Results.Json(new { error = "InsufficientCredentialScope", message = "A credential can't be broader than the one issuing it." }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(body.Identity))
        return Results.BadRequest(new { error = "identity is required: one user or application per credential" });

    try
    {
        var grant = await store.IssueAsync(acting.TenantId, body.AccountId, body.Identity, level, scope, body.ExpiresAt, body.RegionLock ?? [],
            body.CanManage, acting.CredentialId, ct, body.CredentialId);
        return grant is null ? Results.BadRequest(new { error = "no such account in this tenant" }) : Results.Ok(Issued(grant));
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "CredentialIdReused", message = "Credential IDs are never reused." }); // AC8
    }
});

internalApi.MapGet("/credentials", async (HttpContext http, AuthorityStore store, CancellationToken ct) =>
    await ActingAsync(http, store, ct) is { } acting
        ? Results.Ok((await store.ListAsync(acting.TenantId, ct)).Select(Described))
        : Results.StatusCode(403));

internalApi.MapPost("/credentials/{id}/revoke", async (string id, HttpContext http, AuthorityStore store, CancellationToken ct) =>
{
    // A tenant manager, or the credential itself (FR5.17).
    if (await ActingAsync(http, store, ct) is not { } acting || (!acting.CanManage && acting.CredentialId != id)) return Results.StatusCode(403);
    return await store.RevokeAsync(acting.TenantId, id, acting.CredentialId, ct) ? Results.Ok(new { revoked = id }) : Results.NotFound();
});

internalApi.MapPost("/credentials/{id}/rotate", async (string id, RotateRequest? body, HttpContext http, AuthorityStore store, CancellationToken ct) =>
{
    if (await ActingAsync(http, store, ct) is not { } acting || (!acting.CanManage && acting.CredentialId != id)) return Results.StatusCode(403);
    var grant = await store.RotateAsync(acting.TenantId, id, TimeSpan.FromSeconds(Math.Clamp(body?.GraceSeconds ?? 0, 0, 86_400)), acting.CredentialId, ct);
    return grant is null ? Results.NotFound() : Results.Ok(Issued(grant));
});

internalApi.MapPut("/policies/{bucket}", async (string bucket, string? key, PolicyRule[] rules, HttpContext http, AuthorityStore store, CancellationToken ct) =>
{
    if (await ActingAsync(http, store, ct) is not { CanManage: true } acting) return Results.StatusCode(403);
    await store.SetRulesAsync(acting.TenantId, bucket, key, rules.Select(r => r with { TenantId = acting.TenantId, Bucket = bucket, Key = key }).ToList(),
        acting.CredentialId, ct);
    return Results.NoContent();
});

internalApi.MapGet("/audit", async (string? credentialId, long? after, int? limit, HttpContext http, AuthorityStore store, CancellationToken ct) =>
    await ActingAsync(http, store, ct) is { } acting
        ? Results.Ok(await store.AuditAsync(acting.TenantId, credentialId, after ?? 0, Math.Clamp(limit ?? 100, 1, 1000), ct))
        : Results.StatusCode(403));

internalApi.MapGet("/audit/verify", async (HttpContext http, AuthorityStore store, CancellationToken ct) =>
    await ActingAsync(http, store, ct) is { } acting
        ? Results.Ok(new { intact = await store.VerifyChainAsync(acting.TenantId, ct) is null })
        : Results.StatusCode(403));

app.Run();

static AuthorityOptions Opts(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IOptions<AuthorityOptions>>().Value;

// The credential a gateway authenticated (SigV4) and is acting for; it must be active and its tenant active.
static async Task<CredentialGrant?> ActingAsync(HttpContext http, AuthorityStore store, CancellationToken ct)
{
    var id = http.Request.Headers["X-Acting-Credential"].FirstOrDefault();
    if (id is null || await store.GetAsync(id, ct) is not { Status: CredentialStatus.Active } acting) return null;
    if (acting.RevokedAt is { } r && r <= DateTimeOffset.UtcNow || acting.ExpiresAt is { } e && e <= DateTimeOffset.UtcNow) return null;
    var tenants = (await store.SnapshotAsync(ct)).Tenants;
    return tenants.Any(t => t.TenantId == acting.TenantId && t.Status == TenantStatus.Active) ? acting : null;
}

// The secret appears in this response and nowhere else, ever (FR3.11).
static object Issued(CredentialGrant g) => new { credential = Described(g), accessKeyId = g.AccessKeyId, secretAccessKey = g.Secret };

static object Described(CredentialGrant g) => new
{
    credentialId = g.CredentialId, accessKeyId = g.AccessKeyId, tenantId = g.TenantId, accountId = g.AccountId, identity = g.Identity,
    accessLevel = g.AccessLevel.ToString(), scope = g.Scope.ToString(), expiresAt = g.ExpiresAt, regionLock = g.RegionLock, canManage = g.CanManage,
    status = g.Status.ToString(), revokedAt = g.RevokedAt, rotatedFrom = g.RotatedFrom, rotatedTo = g.RotatedTo, createdAt = g.CreatedAt,
};

internal sealed record CreateTenant(string TenantId, string AdminIdentity);

internal sealed record IssueRequest(string Identity, AccessLevel? AccessLevel, string? Bucket, string? Prefix, string? Key, DateTimeOffset? ExpiresAt,
    string[]? RegionLock, bool CanManage, string? AccountId, string? CredentialId);

internal sealed record RotateRequest(int? GraceSeconds);

public partial class Program;
