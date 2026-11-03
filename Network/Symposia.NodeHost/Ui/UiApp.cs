using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Symposia.Network.Node;
using Symposia.NodeHost.Components;
using Symposia.NodeHost.Diagnostics;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Ui;

public sealed record PasswordRequest(string? Password, string? SetupToken = null);

public sealed record SettingsUpdate(HostSettings? Settings, string? RelayerKey);

/// <summary>
/// Password-protected configuration and diagnostics UI (issue #133 FR-E).
/// Reachable on all interfaces; every route except the static shell, session
/// probe, first-run setup, and login requires an authenticated session.
/// </summary>
public static class UiApp
{
    public const int MinPasswordLength = 8;
    private const string LoginPolicy = "login";

    /// <param name="setupToken">
    /// One-time token printed to the host console on first run. Setting the
    /// initial admin password needs it unless the request comes from this
    /// machine, so nobody else on the network can claim a fresh node.
    /// </param>
    public static WebApplication Create(int port, ComponentSupervisor supervisor, SettingsStore settings, SecretStore secrets, string nodeAddress, string setupToken)
    {
        var context = supervisor.Context;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Production",
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ComponentLoggerProvider("ui", context.Logs));
        // Keys are protected by the owner-only directory, not an XML encryptor; don't warn about it on every start.
        builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        builder.Services.AddSingleton(supervisor);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(sp => new StatusService(supervisor, nodeAddress, sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

        // Session cookies survive host restarts: data-protection keys live in an
        // owner-only directory under the data dir (anyone who can read them can forge a session).
        var keysDir = Path.Combine(context.Paths.DataDir, "ui-keys");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(keysDir);
        }
        else
        {
            Directory.CreateDirectory(keysDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
            .SetApplicationName("Symposia.NodeHost");

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = "symposia_node";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
                cookie.SlidingExpiration = true;
                cookie.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                cookie.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            });
        builder.Services.AddAuthorization(auth =>
            auth.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(LoginPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
        });

        var app = builder.Build();

        var webRoot = new ManifestEmbeddedFileProvider(typeof(UiApp).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRoot });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = webRoot });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        var api = app.MapGroup("/api");

        // Long-running applies use the host's lifetime, not the request: closing the
        // browser tab mid-apply must not abandon components half-started.
        var hostLifetime = app.Lifetime.ApplicationStopping;

        api.MapGet("/session", (HttpContext http) => Results.Ok(new
        {
            setupRequired = !secrets.HasPassword,
            setupTokenRequired = !secrets.HasPassword && !IsLocal(http),
            authenticated = http.User.Identity?.IsAuthenticated == true,
        })).AllowAnonymous();

        api.MapPost("/setup", async (PasswordRequest req, HttpContext http) =>
        {
            if (!IsLocal(http) && !CryptographicEquals(req.SetupToken, setupToken))
            {
                return Results.Json(new { error = "Setup from another machine needs the setup token printed in the node host's console." }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (req.Password is null || req.Password.Length < MinPasswordLength)
            {
                return Results.BadRequest(new { error = $"Password must be at least {MinPasswordLength} characters." });
            }

            if (!secrets.TrySetInitialPassword(req.Password))
            {
                return Results.Conflict(new { error = "An admin password is already set." });
            }

            await SignInAsync(http);
            return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting(LoginPolicy);

        api.MapPost("/login", async (PasswordRequest req, HttpContext http) =>
        {
            if (req.Password is null || !secrets.VerifyPassword(req.Password))
            {
                return Results.Unauthorized();
            }

            await SignInAsync(http);
            return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting(LoginPolicy);

        api.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        api.MapGet("/settings", async (CancellationToken ct) => Results.Ok(new
        {
            settings = settings.Effective,
            lockedKeys = settings.LockedKeys,
            relayerKeySet = secrets.HasRelayerKey,
            presets = ChainPresets.All.Select(p => new { name = p.Key, p.Value.Description, deployed = p.Value.IsDeployed }),
            localGatewayFound = settings.Effective.Network.Mode == NetworkMode.Unconfigured
                && await ChainComponent.GatewayRespondsAsync("http://127.0.0.1:8080", ct),
        }));

        api.MapPut("/settings", async (SettingsUpdate update) =>
        {
            if (update.Settings is null)
            {
                return Results.BadRequest(new { error = "settings is required" });
            }

            var errors = Validate(update.Settings);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            if (update.RelayerKey is not null)
            {
                secrets.SetRelayerKey(update.RelayerKey);
            }

            await supervisor.SaveAndApplyAsync(update.Settings, hostLifetime);
            return Results.Ok();
        });

        api.MapGet("/status", async (StatusService status, CancellationToken ct) => Results.Ok(await status.SnapshotAsync(ct)));

        api.MapPost("/components/{name}/restart", async (string name) =>
        {
            try
            {
                await supervisor.RestartAsync(name, hostLifetime);
                return Results.Ok();
            }
            catch (ArgumentException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        return app;
    }

    internal static List<string> Validate(HostSettings s)
    {
        var errors = new List<string>();
        var ports = new List<(string Name, int Port)>();

        if (s.Network.Mode is NetworkMode.LocalDev or NetworkMode.RemoteRpc)
        {
            ports.Add(("gateway", s.Network.GatewayPort));
        }

        if (s.Network.Mode == NetworkMode.LocalDev)
        {
            ports.Add(("anvil", s.Network.AnvilPort));
        }

        if (s.Network.Mode == NetworkMode.Join && !Uri.TryCreate(s.Network.GatewayUrl, UriKind.Absolute, out _))
        {
            errors.Add("Join mode needs a valid gateway URL.");
        }

        if (s.Storage.Enabled)
        {
            ports.Add(("storage", s.Storage.Port));
            CheckEndpoint("Storage", s.Storage.AdvertisedEndpoint, errors);
        }

        if (s.Compute.Enabled)
        {
            ports.Add(("compute", s.Compute.Port));
            CheckEndpoint("Compute", s.Compute.AdvertisedEndpoint, errors);
        }

        errors.AddRange(ports.Where(p => p.Port is < 1 or > 65535).Select(p => $"{p.Name} port {p.Port} is out of range."));
        errors.AddRange(ports.GroupBy(p => p.Port).Where(g => g.Count() > 1)
            .Select(g => $"Port {g.Key} is used by both {string.Join(" and ", g.Select(p => p.Name))}."));

        if (s.Discovery.IntervalSeconds < 1)
        {
            errors.Add("Discovery interval must be at least 1 second.");
        }

        return errors;
    }

    private static void CheckEndpoint(string role, string endpoint, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(endpoint) && NodeEndpoint.Normalize(endpoint) is null)
        {
            errors.Add($"{role} advertised endpoint must be an http(s) base URL like http://10.0.0.5:5180.");
        }
    }

    private static bool IsLocal(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);

    private static bool CryptographicEquals(string? a, string b) =>
        a is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    private static Task SignInAsync(HttpContext http) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme)));
}
