using System.Collections.Concurrent;

namespace Symposia.BlobStorage.Gateway.Routing;

/// <summary>
/// Layer 2 of Requirements/BlobStorage/metadata-architecture.md (#12 FR2.2): an in-memory <c>CID → [node_id]</c>
/// cache built from each storage node's manifest change feed. It's never persisted, and never trusted over a node:
/// on restart it is rebuilt from the feeds (FR2.3), and a node that says it doesn't hold a blob is believed at once.
/// </summary>
/// <remarks>
/// Each node's feed is applied independently and only to that node's own entries, so a stale or lying node can
/// only misstate its own holdings (QA TC2.7/TC2.8); the epoch-root dispute check catches it at Layer 3. Only an
/// Active blob counts as held (TC1.7). One feed is totally ordered (snapshot rows, then events after the snapshot's
/// head), so applying it in arrival order converges on the node's state without per-entry versions.
/// </remarks>
public sealed class RoutingCache(Health.GatewayMetrics? metrics = null)
{
    // node → its held CIDs; the per-node map makes a reset O(that node's holdings).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byCid = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _caughtUp = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    // Holdings learned outside the feeds (peer gateways, chain proofs), kept apart so the per-node counts stay purely
    // feed-derived: the watcher compares them with each node's own Active count (review: Carl #1).
    private readonly ConcurrentDictionary<(string Cid, string Node), DateTimeOffset> _learned = new();

    /// <summary>How long an out-of-band holding is trusted without the node's feed confirming it.</summary>
    public static readonly TimeSpan LearnedTtl = TimeSpan.FromMinutes(10);

    /// <summary>When the cache first became warm; null until then.</summary>
    public DateTimeOffset? WarmSince { get; private set; }

    /// <summary>Created at; the start of the warm-up window (AC3: under 60 seconds).</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Nodes currently in the cache's view (those whose feeds were opened at least once).</summary>
    public IReadOnlyCollection<string> Nodes => _caughtUp.Keys.ToList();

    /// <summary>Every watched node has delivered its snapshot and backlog at least once (FR4.4).</summary>
    public bool IsWarm => !_caughtUp.IsEmpty && _caughtUp.Values.All(c => c);

    /// <summary>Nodes that, by their own feeds, currently hold <paramref name="cid"/>.</summary>
    public IReadOnlyList<string> Holders(string cid)
    {
        var holders = _byCid.TryGetValue(cid, out var fed) ? fed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var now = DateTimeOffset.UtcNow;
        foreach (var ((learnedCid, node), expires) in _learned)
        {
            if (learnedCid != cid) continue;
            if (expires > now) holders.Add(node);
            else _learned.TryRemove((learnedCid, node), out _);
        }

        return [.. holders];
    }

    /// <summary>CIDs <paramref name="nodeId"/> currently holds, by its feed.</summary>
    public int CountFor(string nodeId) => _byNode.TryGetValue(nodeId, out var held) ? held.Count : 0;

    /// <summary>Registers a feed about to be opened, so warmth waits for it.</summary>
    public void Watching(string nodeId) => _caughtUp.TryAdd(nodeId, false);

    /// <summary>Stops counting a node's feed (it left the network); its holdings are dropped.</summary>
    public void Forget(string nodeId)
    {
        Reset(nodeId);
        _caughtUp.TryRemove(nodeId, out _);
        _byNode.TryRemove(nodeId, out _);
    }

    /// <summary>Drops everything learned from <paramref name="nodeId"/>; a snapshot follows.</summary>
    public void Reset(string nodeId)
    {
        lock (_gate)
        {
            if (_byNode.TryGetValue(nodeId, out var held))
            {
                foreach (var cid in held.Keys)
                {
                    RemoveHolder(cid, nodeId);
                }

                held.Clear();
            }
        }

        Publish();
    }

    /// <summary>Applies one PUT (held when Active) or REMOVED event from <paramref name="nodeId"/>'s feed.</summary>
    public void Apply(string nodeId, string cid, bool held)
    {
        lock (_gate)
        {
            var node = _byNode.GetOrAdd(nodeId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            if (held)
            {
                node[cid] = 0;
                _byCid.GetOrAdd(cid, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))[nodeId] = 0;
            }
            else
            {
                node.TryRemove(cid, out _);
                RemoveHolder(cid, nodeId);
            }
        }
    }

    /// <summary>A node's feed has delivered everything up to now.</summary>
    public void CaughtUp(string nodeId)
    {
        _caughtUp[nodeId] = true;
        if (WarmSince is null && IsWarm)
        {
            WarmSince = DateTimeOffset.UtcNow;
        }

        Publish();
    }

    /// <summary>The node said it doesn't hold <paramref name="cid"/> (a read answered NotFound, or a dispute went against it).</summary>
    public void Evict(string nodeId, string cid)
    {
        // Routing stops at once, but the per-node set stays as the node's feed reported it, so the digest compare
        // isn't skewed (review: Frank #2). The next feed event or snapshot for that CID is authoritative again.
        _learned.TryRemove((cid, nodeId), out _);
        lock (_gate)
        {
            RemoveHolder(cid, nodeId);
        }
    }

    /// <summary>A holding learned outside the feed (a peer gateway or an on-chain proof), until the feed says otherwise.</summary>
    public void Learn(string nodeId, string cid) => _learned[(cid, nodeId)] = DateTimeOffset.UtcNow + LearnedTtl;

    private void RemoveHolder(string cid, string nodeId)
    {
        if (_byCid.TryGetValue(cid, out var holders) && holders.TryRemove(nodeId, out _) && holders.IsEmpty)
        {
            _byCid.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, byte>>(cid, holders));
        }
    }

    private void Publish()
    {
        metrics?.RoutingCacheCids.Set(_byCid.Count);
        metrics?.RoutingCacheWarm.Set(IsWarm ? 1 : 0);
    }
}
