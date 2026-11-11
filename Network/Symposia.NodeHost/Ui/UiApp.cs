using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Symposia.Network.Node;
using Symposia.NodeHost.Alerts;
using Symposia.NodeHost.Components;
using Symposia.NodeHost.Diagnostics;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Ui;

public sealed record PasswordRequest(string? Password, string? SetupToken = null);

/// <param name="SmtpPassword">Operator Alerts SMTP password (#196): null keeps the stored one, "" clears it.</param>
public sealed record SettingsUpdate(HostSettings? Settings, string? RelayerKey, string? BlobGatewaySecret = null, string? SmtpPassword = null);

/// <summary>A test send from the Operator Alerts form (#196); a null password uses the stored one.</summary>
public sealed record AlertTestRequest(AlertSettings? Alerts, string? SmtpPassword);

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
        builder.Logging.AddProvider(context.LoggerProvider("ui"));
        // Per-service levels are applied by the provider (live); let everything through to it.
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        // Keys are protected by the owner-only directory, not an XML encryptor; don't warn about it on every start.
        builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        builder.Services.ConfigureHttpJsonOptions(json => ConfigureJson(json.SerializerOptions));
        builder.Services.AddSingleton(supervisor);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(supervisor.Context.Health ?? new NodeHealth(supervisor, nodeAddress));
        builder.Services.AddSingleton<NetworkOverview>();
        builder.Services.AddSingleton<IStorageHistorySource, ChainStorageHistorySource>();
        builder.Services.AddSingleton<NetworkDashboard>();
        builder.Services.AddSingleton(_ => CreateLedger(context, nodeAddress));
        builder.Services.AddSingleton<IAlertMailTransport, SmtpAlertMailTransport>();
        builder.Services.AddHostedService(sp => new AlertNotifier(
            context.Alerts, () => settings.Effective, secrets, sp.GetRequiredService<IAlertMailTransport>(), nodeAddress,
            () => LanAddress.Detect() is { } lan ? $"http://{lan}:{port}" : null,
            context.CreateLogger("alerts", "Symposia.NodeHost.Alerts")));
        builder.Services.AddSingleton(sp => new StatusService(supervisor, nodeAddress, sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), sp.GetRequiredService<NodeHealth>()));

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
                // Browsers scope cookies by host, not port: two hosts on one machine would overwrite each
                // other's session, so the name includes the UI port.
                cookie.Cookie.Name = $"symposia_node_{port}";
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
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = webRoot,
            // The UI changes with every host build; make browsers revalidate so a stale app.js never pairs with new HTML.
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });
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
            blobGatewaySecretSet = secrets.HasBlobGatewaySecret,
            smtpPasswordSet = secrets.HasSmtpPassword,
            lanAddress = LanAddress.Detect(),
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

            if (update.SmtpPassword is not null)
            {
                secrets.SetSmtpPassword(update.SmtpPassword);
            }

            var secretChanged = !string.IsNullOrWhiteSpace(update.BlobGatewaySecret);
            if (secretChanged)
            {
                secrets.SetBlobGatewaySecret(update.BlobGatewaySecret);
            }

            var restarted = await supervisor.SaveAndApplyAsync(update.Settings, hostLifetime);
            if (secretChanged && !restarted.Contains(supervisor.BlobGateway))
            {
                await supervisor.RestartBlobGatewayAsync(hostLifetime);
            }

            return Results.Ok();
        });

        // Operator Alerts test email (#196): one per 10 s host-wide, to the single address in the form.
        var lastTest = DateTimeOffset.MinValue;
        var testGate = new SemaphoreSlim(1, 1);
        api.MapPost("/alerts/test", async (AlertTestRequest req, IAlertMailTransport transport, CancellationToken ct) =>
        {
            var alerts = req.Alerts;
            if (alerts is null || string.IsNullOrEmpty(alerts.Email))
            {
                return Results.BadRequest(new { errors = new[] { "Enter an alert email address first." } });
            }

            var errors = AlertMail.Validate(alerts);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            if (!await testGate.WaitAsync(0, ct))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var password = string.IsNullOrEmpty(req.SmtpPassword) ? secrets.SmtpPassword : req.SmtpPassword;
            try
            {
                if (DateTimeOffset.UtcNow - lastTest < TimeSpan.FromSeconds(10))
                {
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                }

                lastTest = DateTimeOffset.UtcNow;
                await transport.SendAsync(new AlertMailMessage(
                    alerts.Email,
                    string.IsNullOrEmpty(alerts.From) ? alerts.Email : alerts.From,
                    $"[Symposia {AlertMail.ShortAddress(nodeAddress)}] Test alert",
                    $"This is a test of Operator Alerts from node {nodeAddress} on {Environment.MachineName}.\n"), alerts, password, ct);
                return Results.Ok(new { sent = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Results.Ok(new { sent = false, error = AlertMail.Scrub(ex.Message, password) });
            }
            finally
            {
                testGate.Release();
            }
        });

        api.MapGet("/status", async (StatusService status, CancellationToken ct) => Results.Ok(await status.SnapshotAsync(ct)));

        api.MapGet("/network", async (NetworkOverview overview) => Results.Ok(await overview.SnapshotAsync()));

        // Network Dashboard, Symposia Storage Network section (#198).
        api.MapGet("/dashboard/storage", async (NetworkDashboard dashboard, CancellationToken ct) => Results.Ok(await dashboard.StorageAsync(ct)));

        // Stake, reward and penalty transactions of this node, per Node Service (issue #161).
        api.MapGet("/ledger", async (Ledger.LedgerService ledger, string? category, string? service, int? limit, CancellationToken ct) =>
        {
            var snapshot = await ledger.SnapshotAsync(ct);
            return Results.Ok(snapshot with { Entries = Ledger.LedgerService.Filter(snapshot.Entries, category, service, limit).ToList() });
        });
        api.MapGet("/ledger.csv", async (Ledger.LedgerService ledger, string? category, string? service, CancellationToken ct) =>
        {
            var snapshot = await ledger.SnapshotAsync(ct);
            var csv = Ledger.LedgerService.ToCsv(Ledger.LedgerService.Filter(snapshot.Entries, category, service, null));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "symposia-ledger.csv");
        });

        // Public earnings breakdown of every Node Service (issue #159).
        api.MapGet("/node-services", () => Results.Ok(ServiceCatalog.NodeServiceCatalog.All));

        // Issue #181 FR4: redeploy only the LocalDev rewards contracts (anvil state, registrations and
        // ProtocolActivations stay), then restart the chain and everything downstream of it.
        api.MapPost("/chain/reset-rewards", async () =>
        {
            if (supervisor.Context.Settings.Network.Mode != NetworkMode.LocalDev)
            {
                return Results.NotFound(new { error = "Only a Local dev network's rewards contracts can be reset." });
            }

            File.WriteAllText(supervisor.Context.Paths.ResetRewardsFlag, DateTimeOffset.UtcNow.ToString("O"));
            await supervisor.RestartAsync(supervisor.Chain.Name, hostLifetime);
            return Results.Ok();
        });

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

        if (s.Storage.Enabled)
        {
            ports.Add(("storage gRPC (TLS)", s.Storage.GrpcTlsPort));
            if (s.Storage.PlaintextGrpc)
            {
                ports.Add(("storage gRPC", s.Storage.GrpcPort));
            }
        }

        if (s.BlobGateway.Enabled)
        {
            ports.Add(("blob gateway", s.BlobGateway.Port));
            if (s.BlobGateway.WriteQuorum < 1 || s.BlobGateway.MinCopies < s.BlobGateway.WriteQuorum)
            {
                errors.Add("Blob gateway: write quorum must be at least 1 and no more than the minimum copies.");
            }
        }

        errors.AddRange(ports.Where(p => p.Port is < 1 or > 65535).Select(p => $"{p.Name} port {p.Port} is out of range."));
        errors.AddRange(ports.GroupBy(p => p.Port).Where(g => g.Count() > 1)
            .Select(g => $"Port {g.Key} is used by both {string.Join(" and ", g.Select(p => p.Name))}."));

        errors.AddRange(AlertMail.Validate(s.Alerts));

        if (s.Discovery.IntervalSeconds < 1)
        {
            errors.Add("Discovery interval must be at least 1 second.");
        }

        return errors;
    }

    /// <summary>The ledger reads whatever network the chain component resolved; a new network resets it.</summary>
    /// <summary>The API's JSON settings: enums by name, and wei amounts as decimal strings (#202).</summary>
    internal static void ConfigureJson(System.Text.Json.JsonSerializerOptions options)
    {
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.Converters.Add(new BigIntegerStringConverter());
    }

    private static Ledger.LedgerService CreateLedger(HostContext context, string nodeAddress)
    {
        (NetworkInfo Network, Ledger.ILedgerLogSource Source)? current = null;
        Ledger.ILedgerLogSource? Source()
        {
            var n = context.Network;
            if (n is null || string.IsNullOrEmpty(n.RpcUrl) || string.IsNullOrEmpty(n.StakingRegistryAddress) || string.IsNullOrEmpty(n.RewardDistributorAddress))
            {
                return null;
            }

            if (current is not { } c || c.Network != n)
            {
                current = (n, new Ledger.NethereumLedgerLogSource(n.RpcUrl, n.StakingRegistryAddress, n.RewardDistributorAddress));
            }

            return current.Value.Source;
        }

        string? Unavailable() => context.Network switch
        {
            null => "This node isn't connected to a network yet.",
            { RpcUrl: null or "" } => "This host has no direct chain connection (Join mode), so it can't read the ledger yet.",
            { StakingRegistryAddress: null or "" } => "This network hasn't published its staking registry address.",
            { RewardDistributorAddress: null or "" } => "This network has no rewards contracts yet.",
            _ => null,
        };

        // Local anvil has no reorgs; real chains stay a few blocks behind the head.
        // Persisted per (chain, registry), so a restart resumes instead of rescanning (#163).
        Ledger.LedgerStore? Store() => context.Network is { StakingRegistryAddress: { Length: >= 10 } registry } n
            ? new Ledger.LedgerStore(Path.Combine(context.Paths.DataDir, "ledger", $"{n.ChainId}-{registry[2..10].ToLowerInvariant()}"))
            : null;
        return new Ledger.LedgerService(Source, nodeAddress, Unavailable, context.Network?.ChainId == 31337 ? 0UL : 12UL, Store);
    }

    private static void CheckEndpoint(string service, string endpoint, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(endpoint) && NodeEndpoint.Normalize(endpoint) is null)
        {
            errors.Add($"{service} advertised endpoint must be an http(s) base URL like http://10.0.0.5:5180.");
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
