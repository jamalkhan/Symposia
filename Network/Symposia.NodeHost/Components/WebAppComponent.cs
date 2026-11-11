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
    private CancellationTokenSource? _staking;

    /// <summary>The running app's services; null while stopped.</summary>
    public IServiceProvider? Services => _app?.Services;

    protected abstract WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder> configure, HostContext context);

    protected abstract IDictionary<string, string?> Configuration(HostContext context);

    protected abstract int Port(HostContext context);

    protected virtual bool BindsOwnPort => false;

    /// <summary>Roles keep their data in a per-network directory (<see cref="HostContext.RoleDataDir"/>).</summary>
    protected virtual bool UsesRoleDataDir => false;

    /// <summary>Roles that publish this host's health summary for peers' Network tab.</summary>
    protected virtual bool ServesNodeHealth => false;

    /// <summary>Hook to add middleware to the built app before it starts (runs ahead of its endpoints).</summary>
    protected virtual void Secure(WebApplication app)
    {
    }

    protected override string? SkipReason(HostContext context) =>
        context.Network is null ? "Waiting for the network (see chain component)" : null;

    /// <summary>The on-chain Node Service this role stakes on a LocalDev network (#200), and the capacity it stakes for; null = none.</summary>
    protected virtual (byte Service, long Capacity)? StakedService(HostContext context) => null;

    protected override async Task OnStartAsync(HostContext context, CancellationToken ct)
    {
        var dir = context.Paths.Component(Name);
        Directory.CreateDirectory(dir);
        if (UsesRoleDataDir)
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
        if (context.Health is { } health && ServesNodeHealth)
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
        StartLocalDevStaking(context);
    }

    /// <summary>
    /// On a LocalDev network, stakes this role's Node Service in the background so it can earn rewards
    /// locally (#200). Retries every 30 s until the service is Active; never faults the component.
    /// </summary>
    private void StartLocalDevStaking(HostContext context)
    {
        var network = context.Network!;
        if (StakedService(context) is not { } staked || network.ChainId != Symposia.Network.Node.Staking.DevFunding.LocalDevChainId
            || string.IsNullOrEmpty(network.StakingRegistryAddress) || string.IsNullOrEmpty(network.RpcUrl))
        {
            return;
        }

        _staking = new CancellationTokenSource();
        var ct = _staking.Token;
        var logger = context.CreateLogger(Name, "Symposia.NodeHost.LocalDevStaking");
        _ = Task.Run(async () =>
        {
            var identity = new Symposia.Network.Node.NodeIdentity(Microsoft.Extensions.Options.Options.Create(
                new Symposia.Network.Node.NodeNetworkOptions { NodeIdentityKeyPath = context.Paths.NodeKey }));
            identity.EnsureLoadedOrGenerated();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var staker = new Symposia.Network.Node.Staking.NodeServiceStaker(identity, network.ChainId, network.RpcUrl!,
                new Uri(network.GatewayUrl), network.StakingRegistryAddress!, http, logger);
            string? lastProblem = null;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var outcome = await staker.EnsureStakedAsync(staked.Service, staked.Capacity, ct);
                    if (outcome == Symposia.Network.Node.Staking.StakeOutcome.Active)
                    {
                        logger.LogInformation("Node Service {Service} is staked and Active on this LocalDev network.", staked.Service);
                        return;
                    }

                    if (outcome is Symposia.Network.Node.Staking.StakeOutcome.Unavailable or Symposia.Network.Node.Staking.StakeOutcome.Blocked)
                    {
                        logger.LogWarning("Not staking Node Service {Service}: {Outcome} (the LocalDev gateway has no dev funding, or the service is suspended or banned).",
                            staked.Service, outcome);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (ex.Message != lastProblem)
                    {
                        logger.LogWarning("Staking Node Service {Service} failed; retrying: {Reason}", staked.Service, ex.Message);
                        lastProblem = ex.Message;
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, ct);
    }

    protected override async Task OnStopAsync()
    {
        _staking?.Cancel();
        _staking?.Dispose();
        _staking = null;
        var app = _app;
        _app = null;
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
