using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// Static node list from config with background health probing.
/// In production this is replaced by the gossip-based routing table populated from the P2P
/// network and on-chain node registry (Requirements/BlobStorage/metadata-architecture.md).
/// </summary>
public sealed class NodeRegistry : INodeRegistry, IHostedService, IDisposable
{
    private readonly ILogger<NodeRegistry> _logger;
    private readonly List<NodeConnection> _nodes;
    private Timer? _probeTimer;

    public NodeRegistry(IOptions<GatewayOptions> options, ILogger<NodeRegistry> logger)
    {
        _logger = logger;
        _nodes = options.Value.Nodes.Select(url => new NodeConnection(url)).ToList();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Probe immediately on startup so the first request has health data.
        _probeTimer = new Timer(_ => ProbeAll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _probeTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <summary>All configured nodes, regardless of health. Used for reads with fallback.</summary>
    public IReadOnlyList<NodeConnection> All => _nodes;

    /// <summary>
    /// Nodes that have passed a recent health probe. Used for write placement.
    /// Falls back to all nodes on first startup before any probe has completed.
    /// </summary>
    public IReadOnlyList<NodeConnection> Healthy =>
        _nodes.Any(n => n.IsHealthy)
            ? _nodes.Where(n => n.IsHealthy).ToList()
            : _nodes; // bootstrap: assume all healthy until first probe

    /// <summary>
    /// Selects the best available node holding the given CID.
    /// Minimal scoring: prefer healthy nodes, then round-robin.
    /// Full scoring algorithm (latency, tier, load, error rate) is implemented once gossip
    /// data is available (Requirements/BlobStorage/node-selection-for-reads.md).
    /// </summary>
    public NodeConnection? SelectForRead(IEnumerable<string> nodeUrls)
    {
        var candidates = nodeUrls
            .Select(url => _nodes.FirstOrDefault(n => n.Url == url))
            .OfType<NodeConnection>()
            .OrderByDescending(n => n.IsHealthy)
            .ThenByDescending(n => n.LastProbeTime)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private void ProbeAll()
    {
        foreach (var node in _nodes)
        {
            _ = ProbeAsync(node);
        }
    }

    private async Task ProbeAsync(NodeConnection node)
    {
        try
        {
            var response = await node.Client.ProbeAsync(new ProbeRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5));
            node.LastProbe = response;
            node.LastProbeTime = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Node {Url} probe failed: {Message}", node.Url, ex.Message);
            node.LastProbe = null;
        }
    }

    public void Dispose()
    {
        _probeTimer?.Dispose();
        foreach (var node in _nodes) node.Dispose();
    }
}
