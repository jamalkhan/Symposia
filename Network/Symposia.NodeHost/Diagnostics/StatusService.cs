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
public sealed class StatusService(ComponentSupervisor supervisor, string nodeAddress, IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    public async Task<object> SnapshotAsync(CancellationToken ct)
    {
        var context = supervisor.Context;
        var network = context.Network;
        var chain = await CachedAsync("chain", () => ChainAsync(network, ct));
        var registry = await CachedAsync("registry", () => RegistryAsync(ct));
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
            },
            chain,
            registry,
            peers = peers.Select(p => new
            {
                endpoint = p.Endpoint,
                expectedAddress = p.ExpectedAddress,
                address = p.Address,
                status = p.Status.ToString(),
                reason = p.Reason.ToString(),
                lastAttempt = p.LastAttempt,
            }),
            logs = context.Logs.Tail(200),
        };
    }

    private async Task<object?> ChainAsync(NetworkInfo? network, CancellationToken ct)
    {
        if (network?.RpcUrl is null)
        {
            return null;
        }

        var block = await ChainComponent.RpcAsync(network.RpcUrl, "eth_blockNumber", [], ct);
        return new { latestBlock = block?.GetString() is { } hex ? Convert.ToUInt64(hex, 16) : (ulong?)null };
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

    private async Task<object?> CachedAsync(string key, Func<Task<object?>> load)
    {
        if (cache.TryGetValue(key, out object? hit))
        {
            return hit;
        }

        var value = await load();
        cache.Set(key, value, CacheFor);
        return value;
    }
}
