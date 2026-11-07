using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Symposia.Network.Node.Telemetry;

/// <summary>Telemetry settings, bound from the hosting node's own config section under <c>Telemetry</c>.</summary>
public sealed class TelemetryOptions
{
    public bool Enabled { get; set; }

    /// <summary>Epoch genesis (Unix seconds); must equal the chain's <c>epoch.genesisTimestamp</c>.</summary>
    public long GenesisUnixSeconds { get; set; }

    /// <summary>Epoch length; must equal the chain's <c>epoch.length</c>.</summary>
    public long EpochSeconds { get; set; } = 86_400;

    /// <summary>Heartbeat cadence (#75 FR1: 5 minutes).</summary>
    public long CadenceSeconds { get; set; } = 300;

    public int CheckpointsPerEpoch { get; set; } = 24;

    public TelemetrySchedule Schedule => new(GenesisUnixSeconds, EpochSeconds, CadenceSeconds, CheckpointsPerEpoch);

    /// <summary>Telemetry contracts (#158). Setting <c>TelemetryRegistryAddress</c> turns on HTTP gossip, the self-checkpoint fallback and committee duties.</summary>
    public Committee.TelemetryChainOptions Chain { get; set; } = new();

    public Committee.TelemetryGossipOptions Gossip { get; set; } = new();

    public Committee.TelemetryCommitteeOptions Committee { get; set; } = new();

    /// <summary>Blob store for committee batches; only committee members need credentials.</summary>
    public Committee.TelemetryBlobOptions Blobs { get; set; } = new();

    public bool NetworkEnabled => !string.IsNullOrEmpty(Chain.TelemetryRegistryAddress) && !string.IsNullOrEmpty(Chain.RpcUrl);
}

/// <summary>Current raw readings for a heartbeat. Each node role supplies its own.</summary>
public interface IHeartbeatMetricsSource
{
    ValueTask<HeartbeatReadings> ReadAsync(CancellationToken ct);
}

public sealed record HeartbeatReadings(
    ulong ReadThroughputKBps = 0,
    ulong WriteThroughputKBps = 0,
    ulong BandwidthInKbps = 0,
    ulong BandwidthOutKbps = 0,
    uint LatencyMs = 0,
    uint RetrievalMs = 0,
    ulong AvailableBytes = 0,
    ulong UsedBytes = 0);

/// <summary>Liveness-only source for roles without richer metrics yet.</summary>
public sealed class LivenessOnlyMetricsSource : IHeartbeatMetricsSource
{
    public ValueTask<HeartbeatReadings> ReadAsync(CancellationToken ct) => ValueTask.FromResult(new HeartbeatReadings());
}

/// <summary>
/// Emits one signed heartbeat per cadence slot (issue #75 FR1). Ticks are aligned to slot
/// boundaries from the epoch genesis, not to process start, so restarts never produce two
/// samples for one slot (the replay key would drop the second anyway). A failed read or publish
/// is logged and skipped: a missed slot simply counts against compliance, which is the honest outcome.
/// </summary>
public sealed class HeartbeatEmitter(
    NodeIdentity identity,
    ITelemetryGossip gossip,
    IHeartbeatMetricsSource metrics,
    IOptions<TelemetryOptions> options,
    TimeProvider time,
    ILogger<HeartbeatEmitter> logger,
    TelemetryScheduleSource? schedules = null,
    TelemetryDiagnostics? diagnostics = null) : BackgroundService
{
    private uint? _lastSlot;
    private ulong? _lastEpoch;
    private bool _heldLogged;

    /// <summary>Builds, signs and publishes the heartbeat for the slot containing now; null if already sent.</summary>
    public async Task<SignedTelemetry<HeartbeatSample>?> EmitOnceAsync(CancellationToken ct)
    {
        if (schedules is { IsSynced: false })
        {
            // Never stamp a guessed epoch: gossip judges heartbeats by the chain's schedule (#181 FR1).
            if (!_heldLogged)
            {
                logger.LogInformation("Holding heartbeats until the telemetry schedule syncs from chain.");
                _heldLogged = true;
            }

            return null;
        }

        var schedule = schedules?.Current ?? options.Value.Schedule;
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (now < schedule.GenesisUnixSeconds)
        {
            return null;
        }

        var epoch = schedule.EpochAt(now);
        var slot = schedule.SlotAt(now);
        if (_lastEpoch == epoch && _lastSlot == slot)
        {
            return null;
        }

        var r = await metrics.ReadAsync(ct);
        var signed = identity.SignHeartbeat(new HeartbeatSample(identity.NodeId, epoch, slot, now,
            r.ReadThroughputKBps, r.WriteThroughputKBps, r.BandwidthInKbps, r.BandwidthOutKbps,
            r.LatencyMs, r.RetrievalMs, r.AvailableBytes, r.UsedBytes));
        if (await gossip.PublishAsync(signed, ct))
        {
            diagnostics?.Accepted(epoch, slot, time.GetUtcNow());
        }
        else
        {
            logger.LogWarning("Heartbeat for epoch {Epoch} slot {Slot} was rejected by gossip.", epoch, slot);
            diagnostics?.Rejected(epoch, slot, time.GetUtcNow(), "rejected by gossip admission (wrong epoch or slot, or a replay)");
        }

        (_lastEpoch, _lastSlot) = (epoch, slot);
        return signed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cadence = options.Value.CadenceSeconds;
        if (schedules is not null)
        {
            await schedules.WhenSyncedAsync(stoppingToken);
            diagnostics?.ScheduleSynced();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EmitOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Heartbeat emission failed; the slot will count as missed.");
            }

            var now = time.GetUtcNow().ToUnixTimeSeconds();
            var offset = (now - (schedules?.Current ?? options.Value.Schedule).GenesisUnixSeconds) % cadence;
            var wait = TimeSpan.FromSeconds(offset < 0 ? -offset : cadence - offset);
            await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
        }
    }
}

public static class TelemetryServiceExtensions
{
    /// <summary>
    /// Registers 5-minute heartbeat emission when <c>Telemetry:Enabled</c> is set in the node's
    /// section. Requires <see cref="NodeNetworkExtensions.AddNodeNetwork"/>. Uses the in-memory
    /// gossip and a liveness-only metrics source unless the host registered its own first.
    /// </summary>
    public static IServiceCollection AddTelemetryHeartbeats(this IServiceCollection services, IConfigurationSection nodeSection)
    {
        var section = nodeSection.GetSection("Telemetry");
        services.Configure<TelemetryOptions>(section);
        if (!section.GetValue<bool>(nameof(TelemetryOptions.Enabled)))
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        // With a chain, the configured schedule is only a placeholder until the chain's is synced (#181 FR1).
        var chainConfigured = section.GetSection("Chain").Get<Committee.TelemetryChainOptions>() is { TelemetryRegistryAddress.Length: > 0, RpcUrl.Length: > 0 };
        services.TryAddSingleton(sp => new TelemetryScheduleSource(sp.GetRequiredService<IOptions<TelemetryOptions>>().Value.Schedule, requiresSync: chainConfigured));
        services.TryAddSingleton<IHeartbeatMetricsSource, LivenessOnlyMetricsSource>();
        if (chainConfigured)
        {
            Committee.TelemetryNetworkExtensions.AddTelemetryNetwork(services, section);
        }

        services.TryAddSingleton<ITelemetryGossip>(sp =>
        {
            var network = sp.GetRequiredService<IOptions<NodeNetworkOptions>>().Value;
            var telemetry = sp.GetRequiredService<IOptions<TelemetryOptions>>().Value;
            var admission = new TelemetryAdmission(telemetry.Schedule, sp.GetRequiredService<TimeProvider>());
            return new InMemoryTelemetryGossip(network.ChainId, network.NodeRegistryAddress, admission);
        });
        services.AddSingleton<HeartbeatEmitter>();
        services.AddHostedService(sp => sp.GetRequiredService<HeartbeatEmitter>());
        return services;
    }
}
