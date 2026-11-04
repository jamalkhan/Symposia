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

    private readonly IStorageRpcSigner? _signer;
    private readonly HashSet<string> _tlsSeen = new(StringComparer.OrdinalIgnoreCase);

    public NodeRegistry(
        IOptions<GatewayOptions> options,
        GatewayMetadataStore store,
        ILogger<NodeRegistry> logger,
        IStorageRpcSigner? signer = null)
    {
        _signer = signer;
        _options = options;
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Nodes arrive through Sync, fed by the configured IStorageNodeSource (issue #137).
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
                .Select(id => _nodes.FirstOrDefault(n =>
                    n.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
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
    /// Applies a source's current node list (issue #137):
    /// <list type="bullet">
    /// <item>a new ID is added (and, the first time, URL-keyed metadata for it is migrated to its ID);</item>
    /// <item>a known ID at a new URL gets a fresh connection; metadata is keyed by ID, so nothing is rewritten;</item>
    /// <item>an ID missing from the source longer than <see cref="GatewayOptions.NodeGracePeriodSeconds"/> is dropped.
    /// Its replica references stay; the compliance scan stops counting them and repair adds copies elsewhere.</item>
    /// </list>
    /// </summary>
    public void Sync(IReadOnlyList<StorageNodeDescriptor> descriptors)
    {
        var now = DateTimeOffset.UtcNow;
        var added = new List<NodeConnection>();
        var migrations = new List<(string Url, string Id)>();
        var requireTls = _options.Value.RequireStorageTls;
        lock (_lock)
        {
            foreach (var d in descriptors)
            {
                // No downgrade (#142 review), whatever the source: never connect in cleartext when TLS is
                // required, and never to a node in cleartext once it has been seen with a pinned certificate.
                if (d.TlsFingerprint is null && (requireTls || _tlsSeen.Contains(d.Id)))
                {
                    _logger.LogError("Refusing cleartext storage connection to {Node} at {Url} ({Why}).", d.Id, d.Url,
                        requireTls ? "TLS is required" : "it previously served pinned TLS");
                    continue;
                }

                if (d.TlsFingerprint is not null)
                {
                    _tlsSeen.Add(d.Id);
                }

                var existing = _nodes.FirstOrDefault(n => n.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is not null && existing.Url.Equals(d.Url, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.TlsFingerprint, d.TlsFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    existing.LastSeenInSource = now;
                    continue;
                }

                // Another node previously reachable at this URL (address reuse) must not keep it.
                var squatter = _nodes.FirstOrDefault(n => n.Url.Equals(d.Url, StringComparison.OrdinalIgnoreCase)
                    && !n.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase));
                if (squatter is not null)
                {
                    _logger.LogInformation("URL {Url} now belongs to {Node}; dropping the connection for {Previous}.", d.Url, d.Id, squatter.Id);
                    _nodes.Remove(squatter);
                    squatter.Dispose();
                }

                var conn = new NodeConnection(d.Url, d.Id == d.Url ? null : d.Id, _signer, d.TlsFingerprint) { LastSeenInSource = now };
                if (existing is not null)
                {
                    _logger.LogInformation("Storage node {Node} changed from {Old} to {New} (certificate {Fingerprint}).",
                        d.Id, existing.Url, d.Url, d.TlsFingerprint ?? "none");
                    _nodes.Remove(existing);
                    existing.Dispose();
                }
                else
                {
                    if (d.Id != d.Url)
                    {
                        // O(objects) rewrite: done after the lock is released (#137 review).
                        migrations.Add((d.Url, d.Id));
                    }

                    _logger.LogInformation("Added storage node {Node} at {Url}.", d.Id, d.Url);
                }

                _nodes.Add(conn);
                added.Add(conn);
            }

            var grace = TimeSpan.FromSeconds(_options.Value.NodeGracePeriodSeconds);
            foreach (var gone in _nodes.Where(n => now - n.LastSeenInSource > grace).ToList())
            {
                _logger.LogInformation("Storage node {Node} ({Url}) has been missing from its source for over {Grace}; dropping it.",
                    gone.Id, gone.Url, grace);
                _nodes.Remove(gone);
                gone.Dispose();
            }
        }

        foreach (var (url, id) in migrations)
        {
            var migrated = _store.RenameReplicaUrl(url, id);
            if (migrated > 0)
            {
                _logger.LogInformation("Migrated {Count} replica reference(s) from URL {Url} to node ID {Node}.", migrated, url, id);
            }
        }

        foreach (var conn in added)
        {
            _ = ProbeAsync(conn);
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
            node.LastProbeError = null;
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
            node.LastProbeError = ex is Grpc.Core.RpcException rpc ? $"{rpc.StatusCode}: {rpc.Status.Detail}" : ex.Message;
            _logger.LogWarning("Node {Url} probe failed: {Message}", node.Url, node.LastProbeError);
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
