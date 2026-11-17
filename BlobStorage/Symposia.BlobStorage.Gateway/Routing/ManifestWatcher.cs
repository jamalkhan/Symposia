using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Routing;

/// <summary>
/// Subscribes to every known storage node's manifest change feed (WatchManifest, #12 FR2.1/FR2.2) and applies
/// it to the <see cref="RoutingCache"/>. One long-lived stream per node, resumed from the last sequence number
/// after a drop. A node that answers a stale cursor with a reset is resynced from a snapshot, and so is one whose
/// heartbeat digest (its Active count) disagrees with what this gateway holds for it: anti-entropy without a full
/// list on a timer (QA TC2.5). A node without the RPC is left to <see cref="NodeReconciler"/>'s ListBlobs pass.
/// </summary>
public sealed class ManifestWatcher(
    INodeRegistry nodes,
    RoutingCache cache,
    Health.GatewayMetrics metrics,
    IOptions<GatewayOptions> options,
    ILogger<ManifestWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnsupportedBackoff = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Watch> _watches = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Routing.WatchManifests)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            Sync(stoppingToken);
            try
            {
                await Task.Delay(SyncInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var watch in _watches.Values)
        {
            watch.Stop.Cancel();
        }

        await Task.WhenAll(_watches.Values.Select(w => w.Task));
    }

    /// <summary>Starts a feed for each new node and stops the feeds of nodes that left.</summary>
    private void Sync(CancellationToken stoppingToken)
    {
        var current = nodes.All.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, node) in current)
        {
            if (_watches.TryGetValue(id, out var existing) && ReferenceEquals(existing.Node, node))
            {
                continue;
            }

            existing?.Stop.Cancel();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cache.Watching(id);
            // The old feed finishes before the new one starts, so none of its writes land after the new snapshot's reset (review: Frank #3).
            var previous = existing?.Task ?? Task.CompletedTask;
            _watches[id] = new Watch(node, stop, Task.Run(async () =>
            {
                await previous;
                await RunAsync(node, stop.Token);
            }, CancellationToken.None));
        }

        foreach (var (id, watch) in _watches)
        {
            if (!current.ContainsKey(id) && _watches.TryRemove(id, out _))
            {
                watch.Stop.Cancel();
                _ = watch.Task.ContinueWith(_ => cache.Forget(id), TaskScheduler.Default); // after its last write
            }
        }
    }

    private async Task RunAsync(NodeConnection node, CancellationToken ct)
    {
        var position = new FeedPosition();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await FollowAsync(node, position, ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // An N-1 node: count it as caught up so it doesn't hold the cache cold forever.
                logger.LogInformation("Node {Node} has no manifest feed; its replicas are found by reconciliation only.", node.Id);
                cache.CaughtUp(node.Id);
                await Pause(UnsupportedBackoff, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Manifest feed from {Node} dropped at seq {Seq}; reconnecting.", node.Id, position.Cursor);
                await Pause(RetryDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Where a feed resumes: the last seq applied and the incarnation it belongs to. Cursor 0 = resync from a snapshot.</summary>
    internal sealed class FeedPosition
    {
        public long Cursor { get; set; }

        public string FeedId { get; set; } = "";
    }

    /// <summary>Follows one stream until it drops, advancing <paramref name="position"/>.</summary>
    internal async Task FollowAsync(NodeConnection node, FeedPosition position, CancellationToken ct)
    {
        using var call = node.Client.WatchManifest(new WatchManifestRequest { AfterSeq = position.Cursor, FeedId = position.FeedId }, cancellationToken: ct);
        var live = false;
        await foreach (var e in call.ResponseStream.ReadAllAsync(ct))
        {
            if (e.Reset)
            {
                cache.Reset(node.Id);
            }

            if (e.FeedId.Length > 0)
            {
                position.FeedId = e.FeedId;
            }

            switch (e.Kind)
            {
                case ManifestEvent.Types.Kind.Put:
                    cache.Apply(node.Id, e.Cid, held: e.Status == nameof(Domain.BlobStatus.Active));
                    break;
                case ManifestEvent.Types.Kind.Removed:
                    cache.Apply(node.Id, e.Cid, held: false);
                    break;
                case ManifestEvent.Types.Kind.CaughtUp:
                    cache.CaughtUp(node.Id);
                    live = true;
                    break;
                case ManifestEvent.Types.Kind.Heartbeat:
                    // Sent only when the node is idle, so the counts are comparable; a busy node is checked at its next lull.
                    // Only comparable when the digest describes exactly the state this cursor reached.
                    if (e.HeadSeq == position.Cursor && cache.CountFor(node.Id) != e.ActiveCount)
                    {
                        position.Cursor = 0;
                        logger.LogInformation("Routing cache drifted from node {Node} ({Held} vs {Count} active); resyncing from a snapshot.",
                            node.Id, cache.CountFor(node.Id), e.ActiveCount);
                        return;
                    }

                    break;
            }

            if (e.Seq > 0)
            {
                position.Cursor = e.Seq;
                if (live && e.OccurredAtUnixMs > 0)
                {
                    metrics.GossipLag.Observe(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.OccurredAtUnixMs) / 1000.0);
                }
            }
            else if (e.Kind == ManifestEvent.Types.Kind.CaughtUp && e.HeadSeq > 0)
            {
                position.Cursor = Math.Max(position.Cursor, e.HeadSeq);
            }
        }
    }

    private static async Task Pause(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record Watch(NodeConnection Node, CancellationTokenSource Stop, Task Task);
}
