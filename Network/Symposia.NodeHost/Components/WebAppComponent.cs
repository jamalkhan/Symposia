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
            builder.Logging.AddProvider(new ComponentLoggerProvider(Name, context.Logs));
            if (!BindsOwnPort)
            {
                builder.WebHost.UseUrls($"http://0.0.0.0:{Port(context)}");
            }
        }, context);

        Secure(_app);
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
