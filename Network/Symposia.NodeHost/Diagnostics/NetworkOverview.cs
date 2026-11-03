using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Symposia.Network.Node;
using Symposia.NodeHost.Components;

namespace Symposia.NodeHost.Diagnostics;

public sealed record NetworkNodeRow(string Node, string? Endpoint, string? Host, bool Self, bool Registered, StorageHealth Storage, ComputeHealth Compute)
{
    public string? NotRegisteredReason { get; init; }
}

/// <summary>
/// The Network tab: every node in the registry with its self-reported storage and
/// compute health. Remote nodes are polled in parallel with a short timeout and the
/// whole result is cached for 5s, so open tabs cost one round of requests per 5s.
/// </summary>
public sealed class NetworkOverview(ComponentSupervisor supervisor, NodeHealth health, IMemoryCache cache, ILogger<NetworkOverview> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private const int MaxResponseBytes = 16 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = RequestTimeout,
        MaxResponseContentBufferSize = MaxResponseBytes,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<object> SnapshotAsync()
    {
        var network = supervisor.Context.Network;
        var key = network is null ? "network|none" : $"network|{network.GatewayUrl}|{network.ChainId}|{network.NodeRegistryAddress}";
        if (cache.TryGetValue(key, out object? hit) && hit is not null)
        {
            logger.LogTrace("Network overview served from cache ({Key})", key);
            return hit;
        }

        var value = await LoadAsync();
        cache.Set(key, value, CacheFor);
        return value;
    }

    private async Task<object> LoadAsync()
    {
        var self = health.Local();
        var client = supervisor.Network.Services?.GetService<INodeRegistryClient>();
        if (client is null)
        {
            logger.LogDebug("Network overview: network component is {Status}; showing this node only", supervisor.Network.Status);
            return Result(self, [], "This node isn't connected to a network yet. Choose one in Settings.");
        }

        IReadOnlyList<RegisteredNode> members;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                members = await client.ListNodesAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Network overview: could not list registered nodes from the gateway");
                return Result(self, [], $"Gateway unavailable: {ex.Message}");
            }
        }

        logger.LogDebug("Network overview: registry has {Count} node(s); polling their health", members.Count);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var rows = (await Task.WhenAll(members.Select(m => RowAsync(m, self)))).ToList();
        var unreachable = rows.Count(r => r.Storage.Level == HealthLevel.Unknown);
        if (unreachable > 0)
        {
            logger.LogWarning("Network overview: {Unreachable} of {Count} node(s) did not report health", unreachable, rows.Count);
        }

        logger.LogDebug("Network overview built in {ElapsedMs} ms", started.ElapsedMilliseconds);
        return Result(self, rows, null);
    }

    /// <summary>This node is always listed, even before it registers, with the reason it isn't on-chain yet.</summary>
    private object Result(NodeHealthReport self, List<NetworkNodeRow> rows, string? error)
    {
        if (!rows.Any(r => r.Self))
        {
            var reason = supervisor.Context.PrimaryEndpoint is null
                ? "Not registered: turn on Storage or Compute in Settings so this node has an endpoint to register."
                : "Not registered on-chain yet (see Diagnostics → On-chain registration).";
            logger.LogDebug("Network overview: this node is not in the registry ({Reason})", reason);
            rows.Insert(0, new NetworkNodeRow(self.Node, supervisor.Context.PrimaryEndpoint, null, true, false,
                self.Storage, self.Compute) { NotRegisteredReason = reason });
        }

        return new { error, nodes = rows };
    }

    private async Task<NetworkNodeRow> RowAsync(RegisteredNode member, NodeHealthReport self)
    {
        var host = Uri.TryCreate(member.Endpoint, UriKind.Absolute, out var uri) ? uri.Host : null;
        if (string.Equals(member.Node, self.Node, StringComparison.OrdinalIgnoreCase))
        {
            return new NetworkNodeRow(member.Node, member.Endpoint, host, true, true, self.Storage, self.Compute);
        }

        if (uri is null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("Node {Node} registered an invalid endpoint {Endpoint}", member.Node, member.Endpoint);
            return Unknown(member, host, "Registered endpoint is not a valid URL");
        }

        var url = new Uri(uri, NodeHealth.Path);
        var started = System.Diagnostics.Stopwatch.StartNew();
        logger.LogTrace("GET {Url} for node {Node}", url, member.Node);
        try
        {
            var report = await Http.GetFromJsonAsync<NodeHealthReport>(url, Json);
            if (report is null || !string.Equals(report.Node, member.Node, StringComparison.OrdinalIgnoreCase))
            {
                // A different node answering at this endpoint must not be shown as this one's health.
                logger.LogWarning("Health at {Url} answered for node {Answered}, expected {Node}", url, report?.Node, member.Node);
                return Unknown(member, host, "Endpoint answered for a different node");
            }

            logger.LogDebug("Node {Node} at {Url}: storage {Storage}, compute {Compute} ({ElapsedMs} ms)",
                member.Node, url, report.Storage.Level, report.Compute.Level, started.ElapsedMilliseconds);
            return new NetworkNodeRow(member.Node, member.Endpoint, host, false, true, report.Storage, report.Compute);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            var reason = ex is TaskCanceledException ? "Timed out" : "Unreachable";
            logger.LogWarning("Health check of node {Node} at {Url} failed after {ElapsedMs} ms: {Reason} ({Error})",
                member.Node, url, started.ElapsedMilliseconds, reason, ex.Message);
            return Unknown(member, host, reason);
        }
    }

    private static NetworkNodeRow Unknown(RegisteredNode member, string? host, string reason) =>
        new(member.Node, member.Endpoint, host, false, true,
            new StorageHealth(false, HealthLevel.Unknown, null, null, reason),
            new ComputeHealth(false, HealthLevel.Unknown, null, reason));
}
