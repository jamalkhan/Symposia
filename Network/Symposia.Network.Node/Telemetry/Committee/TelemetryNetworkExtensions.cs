using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Symposia.Network.Node.Telemetry.Committee;

public static class TelemetryNetworkExtensions
{
    /// <summary>
    /// Wires the #158 telemetry network: HTTP gossip in place of the in-process store, the
    /// self-checkpoint fallback, and committee duties (which only act if this node is on the
    /// registry's committee). Called by <see cref="TelemetryServiceExtensions.AddTelemetryHeartbeats"/>
    /// when <c>Telemetry:Chain</c> is configured. Relay calls go to the node's Blockchain Gateway.
    /// </summary>
    public static IServiceCollection AddTelemetryNetwork(this IServiceCollection services, IConfigurationSection telemetrySection)
    {
        services.Configure<TelemetryGossipOptions>(telemetrySection.GetSection("Gossip"));
        services.Configure<TelemetryCommitteeOptions>(telemetrySection.GetSection("Committee"));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new TelemetryScheduleSource(Telemetry(sp).Schedule));
        services.AddHttpClient(nameof(TelemetryGossipNode), c => c.Timeout = TimeSpan.FromSeconds(10));

        services.AddSingleton<ITelemetrySigner, NodeIdentityTelemetrySigner>();
        // TryAdd: a Node Host supplies its own host-wide peer view before this runs (#207).
        services.TryAddSingleton<ITelemetryPeers, PeerTableTelemetryPeers>();
        services.AddSingleton(sp => new TelemetryEip712(Network(sp).ChainId, Telemetry(sp).Chain.TelemetryRegistryAddress));
        services.AddSingleton(sp =>
        {
            var telemetry = Telemetry(sp);
            var admission = new TelemetryAdmission(telemetry.Schedule, sp.GetRequiredService<TimeProvider>())
            {
                Source = sp.GetRequiredService<TelemetryScheduleSource>(),
            };
            return new InMemoryTelemetryGossip(Network(sp).ChainId, Network(sp).NodeRegistryAddress, admission);
        });
        services.AddSingleton(sp => new TelemetryGossipNode(
            sp.GetRequiredService<InMemoryTelemetryGossip>(), sp.GetRequiredService<ITelemetryPeers>(), sp.GetRequiredService<ITelemetrySigner>(),
            sp.GetRequiredService<TelemetryEip712>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<TelemetryGossipOptions>>(),
            Network(sp).ChainId, sp.GetService<TelemetryDiagnostics>(), sp.GetRequiredService<TelemetryScheduleSource>(),
            sp.GetService<ILogger<TelemetryGossipNode>>()));
        services.AddSingleton<ITelemetryGossip>(sp => sp.GetRequiredService<TelemetryGossipNode>());
        services.AddHostedService<TelemetryGossipFlusher>();

        services.AddSingleton<ITelemetryChain>(sp => new NethereumTelemetryChain(Telemetry(sp).Chain, Telemetry(sp).CadenceSeconds));
        services.AddSingleton<ITelemetryRelay>(sp => new HttpTelemetryRelay(new HttpClient
        {
            BaseAddress = new Uri(Network(sp).BlockchainGatewayUrl),
            Timeout = TimeSpan.FromSeconds(60),
        }));
        services.AddSingleton<ITelemetryBlobStore>(sp =>
        {
            var blobs = Telemetry(sp).Blobs;
            ITelemetryBlobStore? remote = string.IsNullOrEmpty(blobs.Endpoint) ? null : new S3TelemetryBlobStore(new HttpClient(), blobs);
            return new CachingTelemetryBlobStore(string.IsNullOrEmpty(blobs.CacheDirectory) ? null : blobs.CacheDirectory, remote);
        });
        services.AddSingleton(sp => new SelfCheckpointWorker(
            sp.GetRequiredService<ITelemetryChain>(), sp.GetRequiredService<ITelemetryRelay>(), sp.GetRequiredService<InMemoryTelemetryGossip>(),
            sp.GetRequiredService<ITelemetrySigner>(), sp.GetRequiredService<TelemetryEip712>(), Network(sp).ChainId, Network(sp).NodeRegistryAddress,
            sp.GetRequiredService<ILogger<SelfCheckpointWorker>>()));
        services.AddSingleton(sp => new CommitteeWorker(
            sp.GetRequiredService<ITelemetryChain>(), sp.GetRequiredService<ITelemetryRelay>(), sp.GetRequiredService<ITelemetryBlobStore>(),
            sp.GetRequiredService<TelemetryGossipNode>(), sp.GetRequiredService<ITelemetrySigner>(), sp.GetRequiredService<TelemetryEip712>(),
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<TelemetryCommitteeOptions>>(), Network(sp).ChainId,
            Network(sp).NodeRegistryAddress, sp.GetRequiredService<ILogger<CommitteeWorker>>()));
        services.AddHostedService<TelemetryCommitteeService>();
        return services;
    }

    private static TelemetryOptions Telemetry(IServiceProvider sp) => sp.GetRequiredService<IOptions<TelemetryOptions>>().Value;

    private static NodeNetworkOptions Network(IServiceProvider sp) => sp.GetRequiredService<IOptions<NodeNetworkOptions>>().Value;
}

/// <summary>
/// Runs the self-checkpoint fallback and committee duties on a poll (#158). Each pass is
/// independent and idempotent, so one failing (an RPC blip, a relay error) is logged and retried
/// on the next tick without blocking the other. Every pass first syncs the shared heartbeat
/// schedule from the EpochController, so configured genesis/length can't drift from the chain.
/// </summary>
public sealed class TelemetryCommitteeService(
    SelfCheckpointWorker fallback,
    CommitteeWorker committee,
    ITelemetryChain chain,
    TelemetryScheduleSource schedules,
    IOptions<TelemetryCommitteeOptions> options,
    ILogger<TelemetryCommitteeService> logger,
    TelemetryDiagnostics? diagnostics = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)));
        do
        {
            await RunGuardedAsync("schedule sync", SyncScheduleAsync, stoppingToken);
            await RunGuardedAsync("self-checkpoint", fallback.RunOnceAsync, stoppingToken);
            if (options.Value.Enabled)
            {
                await RunGuardedAsync("committee", committee.RunOnceAsync, stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private TelemetrySchedule? _invalidLogged;

    private async Task SyncScheduleAsync(CancellationToken ct)
    {
        var onChain = await chain.ScheduleAsync(ct);
        var step = (long)onChain.CheckpointsPerEpoch * onChain.CadenceSeconds;
        if (step <= 0 || onChain.EpochSeconds % step != 0)
        {
            // Mirrors DeployRewards' check: every window must hold whole heartbeats, or the chain under-counts them.
            if (_invalidLogged != onChain)
            {
                logger.LogError("The chain's telemetry schedule ({Epoch}s epochs, {Windows} windows) doesn't fit {Cadence}s heartbeats; {Action}.",
                    onChain.EpochSeconds, onChain.CheckpointsPerEpoch, onChain.CadenceSeconds,
                    schedules.IsSynced ? "keeping the previous schedule" : "holding heartbeats");
                _invalidLogged = onChain;
            }

            diagnostics?.ScheduleInvalid($"{onChain.EpochSeconds}s epochs with {onChain.CheckpointsPerEpoch} windows don't fit {onChain.CadenceSeconds}s heartbeats");
            return;
        }

        if (onChain != schedules.Current || !schedules.IsSynced)
        {
            logger.LogInformation("Telemetry schedule synced from chain: genesis {Genesis}, epoch {Epoch}s, {Windows} windows.",
                onChain.GenesisUnixSeconds, onChain.EpochSeconds, onChain.CheckpointsPerEpoch);
        }

        schedules.Publish(onChain);
    }

    private async Task RunGuardedAsync(string name, Func<CancellationToken, Task> pass, CancellationToken ct)
    {
        try
        {
            await pass(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Telemetry {Pass} pass failed; retrying next poll.", name);
        }
    }
}
