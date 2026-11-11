using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Symposia.Network.Node;
using Symposia.NodeHost.Components;

namespace Symposia.NodeHost.Diagnostics;

/// <summary>
/// Builds the diagnostics snapshot (FR-E4). Chain and registry reads are
/// cached for 5s so any number of open browser tabs polling every 5s cause at
/// most one round of RPC/gateway calls per 5s (QA traffic-shape budget).
/// </summary>
public sealed class StatusService(ComponentSupervisor supervisor, string nodeAddress, IMemoryCache cache, NodeHealth health)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    public async Task<object> SnapshotAsync(CancellationToken ct)
    {
        var context = supervisor.Context;
        var network = context.Network;
        // Keyed by network so a mode switch never shows the previous network's data; loads use
        // their own timeout rather than the first caller's request token, since the result is shared.
        var scope = network is null ? "none" : $"{network.GatewayUrl}|{network.ChainId}|{network.NodeRegistryAddress}";
        var chain = await CachedAsync($"chain|{scope}", load => ChainAsync(network, load));
        var registry = await CachedAsync($"registry|{scope}", RegistryAsync);
        var rewards = await CachedAsync($"rewards|{scope}", load => RewardsAsync(network, load));
        var telemetry = context.Telemetry.Current;
        var peers = supervisor.Network.Services?.GetService<PeerTable>()?.Snapshot() ?? [];

        return new
        {
            node = nodeAddress,
            dataDir = context.Paths.DataDir,
            mode = context.Settings.Network.Mode.ToString(),
            primaryEndpoint = context.PrimaryEndpoint,
            components = supervisor.All.Select(c => new { name = c.Name, status = c.Status.ToString(), reason = c.Reason }),
            network = network is null ? null : new
            {
                gatewayUrl = network.GatewayUrl,
                chainId = network.ChainId,
                nodeRegistryAddress = network.NodeRegistryAddress,
                epochRootRegistryAddress = network.EpochRootRegistryAddress,
                rpcUrl = network.RpcUrl,
                rpcSource = network.RpcSource.ToString(),
                rewardsContracts = new[]
                {
                    ("RewardDistributor", network.RewardDistributorAddress), ("EpochController", network.EpochControllerAddress),
                    ("TelemetryRegistry", network.TelemetryRegistryAddress), ("StakingRegistry", network.StakingRegistryAddress),
                }.Where(c => !string.IsNullOrEmpty(c.Item2)).Select(c => c.Item1),
            },
            chain,
            registry,
            rewards,
            rewardsStale = context.RewardsStale,
            telemetry = context.Settings.Storage.Enabled && context.Settings.Storage.Heartbeats ? new
            {
                configured = !string.IsNullOrEmpty(network?.TelemetryRegistryAddress) && network?.RpcUrl is not null,
                scheduleSynced = telemetry.ScheduleSynced,
                scheduleProblem = telemetry.ScheduleProblem,
                lastAccepted = telemetry.LastAccepted,
                lastRejected = telemetry.LastRejected,
                gossip = new
                {
                    recognizedPeers = context.Telemetry.Gossip.RecognizedPeers,
                    lastPushSucceeded = context.Telemetry.Gossip.LastPushSucceeded,
                    failedPushes = context.Telemetry.Gossip.FailedPushes,
                    warning = context.Telemetry.GossipWarning(DateTimeOffset.UtcNow,
                        peers.Count(p => !string.Equals(p.ExpectedAddress ?? p.Address, nodeAddress, StringComparison.OrdinalIgnoreCase))),
                },
            } : null,
            peers = peers.Select(p => new
            {
                endpoint = p.Endpoint,
                expectedAddress = p.ExpectedAddress,
                address = p.Address,
                status = p.Status.ToString(),
                reason = p.Reason.ToString(),
                lastAttempt = p.LastAttempt,
            }),
            blobGateway = health.Local().Gateway,
            storageCapacity = health.StorageCapacity(),
            activeAlerts = context.Alerts.Active,
            logs = context.Logs.Tail(200),
        };
    }

    private async Task<object?> ChainAsync(NetworkInfo? network, CancellationToken ct)
    {
        if (network?.RpcUrl is null)
        {
            return null;
        }

        try
        {
            var block = await ChainComponent.RpcAsync(network.RpcUrl, "eth_blockNumber", [], ct);
            return new { latestBlock = block?.GetString() is { } hex ? Convert.ToUInt64(hex, 16) : (ulong?)null };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            return new { error = $"Unexpected RPC response: {ex.Message}" };
        }
    }

    private async Task<object?> RegistryAsync(CancellationToken ct)
    {
        var client = supervisor.Network.Services?.GetService<INodeRegistryClient>();
        if (client is null)
        {
            return null;
        }

        try
        {
            var self = await client.GetStatusAsync(nodeAddress, ct);
            var members = await client.ListNodesAsync(ct);
            return new
            {
                registered = self.Registered,
                endpoint = self.Endpoint,
                nonce = self.Nonce,
                members = members.Select(m => new { node = m.Node, endpoint = m.Endpoint, self = string.Equals(m.Node, nodeAddress, StringComparison.OrdinalIgnoreCase) }),
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new { error = $"Gateway unavailable: {ex.Message}" };
        }
    }

    /// <summary>
    /// Where this host's rewards epoch stands (issue #181 FR5): the current epoch, when it ends, when
    /// it can first be sealed (the telemetry deadline, which already includes finality) and whether
    /// this node is on the telemetry committee. Cached like every chain read.
    /// </summary>
    private async Task<object?> RewardsAsync(NetworkInfo? network, CancellationToken ct)
    {
        if (network?.RpcUrl is null || string.IsNullOrEmpty(network.TelemetryRegistryAddress) || string.IsNullOrEmpty(network.EpochControllerAddress))
        {
            return null;
        }

        try
        {
            var chain = new Symposia.Network.Node.Telemetry.Committee.NethereumTelemetryChain(new()
            {
                RpcUrl = network.RpcUrl,
                TelemetryRegistryAddress = network.TelemetryRegistryAddress,
                EpochControllerAddress = network.EpochControllerAddress,
            });
            var now = await chain.NowAsync(ct);
            var schedule = await chain.ScheduleAsync(ct);
            var (members, threshold) = await chain.CommitteeAsync(ct);
            var epoch = now < schedule.GenesisUnixSeconds ? 0 : schedule.EpochAt(now);
            return new
            {
                epoch,
                epochSeconds = schedule.EpochSeconds,
                epochEndsAt = DateTimeOffset.FromUnixTimeSeconds(schedule.EpochStart(epoch) + schedule.EpochSeconds),
                sealableFrom = DateTimeOffset.FromUnixTimeSeconds(await chain.TelemetryDeadlineAsync(epoch, ct)),
                committeeMember = members.Any(m => string.Equals(m, nodeAddress, StringComparison.OrdinalIgnoreCase)),
                committeeSize = members.Count,
                threshold,
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new { error = $"Rewards contracts unavailable: {ex.Message}" };
        }
    }

    private async Task<object?> CachedAsync(string key, Func<CancellationToken, Task<object?>> load)
    {
        if (cache.TryGetValue(key, out object? hit))
        {
            return hit;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        object? value;
        try
        {
            value = await load(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            value = new { error = "Timed out" };
        }

        cache.Set(key, value, CacheFor);
        return value;
    }
}
