using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Symposia.NodeHost.Diagnostics;

namespace Symposia.NodeHost.Components;

/// <summary>
/// A component that hosts one existing app (gateway, storage, compute) as its
/// own <see cref="WebApplication"/> — its own DI container and Kestrel port —
/// inside the Node Host process, configured entirely in memory.
/// </summary>
public abstract class WebAppComponent : HostComponent
{
    private WebApplication? _app;

    /// <summary>The running app's services; null while stopped.</summary>
    public IServiceProvider? Services => _app?.Services;

    protected abstract WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder> configure, HostContext context);

    protected abstract IDictionary<string, string?> Configuration(HostContext context);

    protected abstract int Port(HostContext context);

    protected virtual bool BindsOwnPort => false;

    /// <summary>Hook to add middleware to the built app before it starts (runs ahead of its endpoints).</summary>
    protected virtual void Secure(WebApplication app)
    {
    }

    protected override string? SkipReason(HostContext context) =>
        context.Network is null ? "Waiting for the network (see chain component)" : null;

    protected override async Task OnStartAsync(HostContext context, CancellationToken ct)
    {
        var dir = context.Paths.Component(Name);
        Directory.CreateDirectory(dir);
        if (Name is "storage" or "compute")
        {
            Directory.CreateDirectory(context.RoleDataDir(Name));
        }
        var options = new WebApplicationOptions { ContentRootPath = dir, EnvironmentName = "Production" };

        _app = Build(options, builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(Configuration(context));
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(context.LoggerProvider(Name));
            // Per-service levels are applied by the provider (live); let everything through to it.
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            if (!BindsOwnPort)
            {
                builder.WebHost.UseUrls($"http://0.0.0.0:{Port(context)}");
            }
        }, context);

        Secure(_app);
        if (context.Health is { } health && Name is "storage" or "compute")
        {
            // Public, read-only health summary peers poll for their Network tab.
            var healthLogger = context.CreateLogger(Name, "Symposia.NodeHost.NodeHealth");
            _app.MapGet(NodeHealth.Path, (HttpContext http) =>
            {
                var report = health.Local();
                healthLogger.LogTrace("Health requested by {Remote}: storage {Storage}, compute {Compute}",
                    http.Connection.RemoteIpAddress, report.Storage.Level, report.Compute.Level);
                return Results.Ok(report);
            });
        }

        await _app.StartAsync(ct);
    }

    protected override async Task OnStopAsync()
    {
        var app = _app;
        _app = null;
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
