using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// Live gRPC connection pool for storage nodes, with periodic health probing and dynamic add/remove.
///
/// Node sources (in priority order for dedup):
///  1. appsettings.json Gateway:Nodes array — always loaded at startup.
///  2. Nodes persisted in the gateway SQLite (added via /admin/nodes at runtime).
///
/// Reconnect detection: when a node's health transitions from false → true, its
/// <see cref="NodeConnection.JustReconnected"/> flag is set so <see cref="NodeReconciler"/>
/// can schedule a CID reconciliation scan.
///
/// See Requirements/BlobStorage/gateway-architecture.md#node-health-cache.
/// </summary>
public sealed class NodeRegistry : INodeRegistry, IHostedService, IDisposable
{
    private readonly ILogger<NodeRegistry> _logger;
    private readonly IOptions<GatewayOptions> _options;
    private readonly GatewayMetadataStore _store;
    private readonly Lock _lock = new();
    private readonly List<NodeConnection> _nodes = [];
    private Timer? _probeTimer;

    public NodeRegistry(
        IOptions<GatewayOptions> options,
        GatewayMetadataStore store,
        ILogger<NodeRegistry> logger)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Merge config nodes + DB-persisted nodes (deduped by URL).
        var configUrls = _options.Value.Nodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dbUrls = _store.GetPersistedNodes().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allUrls = configUrls.Union(dbUrls, StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            foreach (var url in allUrls)
            {
                if (!_nodes.Any(n => n.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                    _nodes.Add(new NodeConnection(url));
            }
        }

        // Probe immediately so the first request has health data.
        _probeTimer = new Timer(_ => ProbeAll(), null, TimeSpan.Zero,
            TimeSpan.FromSeconds(Math.Max(1, _options.Value.ProbeIntervalSeconds)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _probeTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    // ── INodeRegistry ─────────────────────────────────────────────────────────

    public IReadOnlyList<NodeConnection> All
    {
        get { lock (_lock) return [.._nodes]; }
    }

    public IReadOnlyList<NodeConnection> Healthy
    {
        get
        {
            lock (_lock)
            {
                var healthy = _nodes.Where(n => n.IsHealthy).ToList();
                // Bootstrap: assume all healthy until the first probe completes.
                return healthy.Count > 0 ? healthy : [.._nodes];
            }
        }
    }

    /// <summary>
    /// Selects the best available node that holds the given CID.
    /// Prefers healthy nodes; falls back to any known node for resilience.
    /// Full scoring (latency, tier, load, error rate) requires gossip data —
    /// see Requirements/BlobStorage/node-selection-for-reads.md.
    /// </summary>
    public NodeConnection? SelectForRead(IEnumerable<string> nodeUrls)
    {
        lock (_lock)
        {
            return nodeUrls
                .Select(url => _nodes.FirstOrDefault(n =>
                    n.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                .OfType<NodeConnection>()
                .OrderByDescending(n => n.IsHealthy)
                .ThenByDescending(n => n.LastProbeTime)
                .FirstOrDefault();
        }
    }

    // ── Dynamic add/remove ────────────────────────────────────────────────────

    /// <summary>Registers a new node at runtime and persists it to the database.</summary>
    public NodeConnection AddNode(string url)
    {
        lock (_lock)
        {
            var existing = _nodes.FirstOrDefault(n => n.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;

            var conn = new NodeConnection(url);
            _nodes.Add(conn);
            _store.PersistNode(url, "admin");
            _ = ProbeAsync(conn); // probe immediately so health is known
            return conn;
        }
    }

    /// <summary>Deregisters a node at runtime and removes it from the database.</summary>
    public bool RemoveNode(string url)
    {
        lock (_lock)
        {
            var conn = _nodes.FirstOrDefault(n => n.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (conn is null) return false;

            _nodes.Remove(conn);
            _store.RemovePersistedNode(url);
            conn.Dispose();
            return true;
        }
    }

    /// <summary>
    /// Adds storage nodes discovered in the on-chain registry (issue #135), keyed by gRPC URL,
    /// and keeps each one's expected node address current. Nodes are not persisted (the registry
    /// is the source of truth) and are never removed here: one that disappears simply fails its
    /// probes and is treated as offline, so its replicas aren't written off early.
    /// </summary>
    public void SyncFromRegistry(IReadOnlyDictionary<string, string> grpcUrlToNodeId)
    {
        lock (_lock)
        {
            foreach (var (url, nodeId) in grpcUrlToNodeId)
            {
                // Same node, new address (DHCP change, LAN endpoint set later): move its replicas to the
                // new URL instead of leaving a dead entry that still "holds" them (issue #135 review).
                var moved = _nodes.FirstOrDefault(n => string.Equals(n.ExpectedNodeId, nodeId, StringComparison.OrdinalIgnoreCase)
                    && !n.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
                if (moved is not null)
                {
                    var renamed = _store.RenameReplicaUrl(moved.Url, url);
                    _logger.LogInformation("Storage node {Node} moved from {Old} to {New}; re-pointed {Count} object(s).",
                        nodeId, moved.Url, url, renamed);
                    _nodes.Remove(moved);
                    moved.Dispose();
                }

                var existing = _nodes.FirstOrDefault(n => n.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    if (!string.Equals(existing.ExpectedNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Storage node at {Url} is now registered to {Node}.", url, nodeId);
                        existing.ExpectedNodeId = nodeId;
                    }

                    continue;
                }

                var conn = new NodeConnection(url, nodeId);
                _nodes.Add(conn);
                _logger.LogInformation("Added storage node {Node} at {Url} from the registry.", nodeId, url);
                _ = ProbeAsync(conn);
            }
        }
    }

    /// <summary>Raised when a node becomes healthy (new or back online); used to start a repair sweep at once.</summary>
    public event Action<NodeConnection>? NodeBecameHealthy;

    // ── Health probing ────────────────────────────────────────────────────────

    private void ProbeAll()
    {
        IReadOnlyList<NodeConnection> snapshot;
        lock (_lock) snapshot = [.._nodes];
        foreach (var node in snapshot)
            _ = ProbeAsync(node);
    }

    private async Task ProbeAsync(NodeConnection node)
    {
        var wasHealthy = node.IsHealthy;
        try
        {
            var response = await node.Client.ProbeAsync(new ProbeRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5));
            if (node.ExpectedNodeId is { } expected && !string.Equals(response.NodeId, expected, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Node at {Url} answered as {Actual} but the registry lists {Expected}; not using it.",
                    node.Url, response.NodeId, expected);
                node.MarkUnhealthy();
                return;
            }

            node.LastProbe = response;
            node.LastProbeTime = DateTimeOffset.UtcNow;
            _logger.LogDebug("Node {Url} probe ok: {Used} bytes used, {Available} available, {Blobs} blobs.",
                node.Url, response.UsedStorageBytes, response.AvailableStorageBytes, response.BlobCount);

            if (!wasHealthy && node.IsHealthy)
            {
                _logger.LogInformation("Node {Url} came back online; flagging for reconciliation.", node.Url);
                node.JustReconnected = true;
                NodeBecameHealthy?.Invoke(node);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Node {Url} probe failed: {Message}", node.Url, ex.Message);
            if (node.IsHealthy || node.LastUnhealthyAt == DateTimeOffset.MinValue)
                node.LastUnhealthyAt = DateTimeOffset.UtcNow;
            node.LastProbe = null;
        }
    }

    public void Dispose()
    {
        _probeTimer?.Dispose();
        lock (_lock)
        {
            foreach (var node in _nodes) node.Dispose();
        }
    }
}
