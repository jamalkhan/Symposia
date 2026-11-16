using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Repair;

/// <summary>
/// Per-node token buckets capping background replication traffic (#8 FR3.3, AC11): repair and penalty
/// pre-replication both draw from the same bucket for a node, so two trigger sources can't double-spend
/// it. The rate is <see cref="RedundancyOptions.BandwidthBudgetPercent"/> of the node's upload capacity,
/// relaxed to <see cref="RedundancyOptions.RecoveryBandwidthBudgetPercent"/> in penalty stage 3–4. A bucket
/// holds one second of its rate, so bursts never exceed the budget by more than that.
/// </summary>
public sealed class BandwidthBudget(IOptions<GatewayOptions> options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    private sealed class Bucket
    {
        public double Tokens;
        public long LastRefill;
        public long Spent;
    }

    /// <summary>Bytes per second background replication may use on <paramref name="node"/>.</summary>
    public double RateFor(NodeConnection node)
    {
        var o = options.Value.Redundancy;
        var capacity = node.UploadBytesPerSecond > 0 ? node.UploadBytesPerSecond : o.DefaultNodeUploadBytesPerSecond;
        var percent = node.PenaltyStage >= 3 ? o.RecoveryBandwidthBudgetPercent : o.BandwidthBudgetPercent;
        return Math.Max(1, capacity * Math.Clamp(percent, 1, 100) / 100.0);
    }

    /// <summary>Waits until <paramref name="bytes"/> fit every node's budget, then charges them all.</summary>
    public async Task AcquireAsync(IReadOnlyList<NodeConnection> nodes, long bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var now = _clock.GetTimestamp();
                var buckets = nodes.Select(n => (Node: n, Bucket: Refill(n, now))).ToList();
                // A chunk bigger than one second of budget is let through on a full bucket and paid back
                // as debt (the bucket goes negative), so the average rate still holds.
                var shortest = buckets.Select(b => (Math.Min(bytes, RateFor(b.Node)) - b.Bucket.Tokens) / RateFor(b.Node)).Max();
                if (shortest <= 0)
                {
                    foreach (var (_, bucket) in buckets)
                    {
                        bucket.Tokens -= bytes;
                        bucket.Spent += bytes;
                    }

                    return;
                }

                wait = TimeSpan.FromSeconds(Math.Min(shortest, 1));
            }

            await Task.Delay(wait, _clock, ct);
        }
    }

    /// <summary>Bytes charged to a node so far (diagnostics, tests).</summary>
    public long Spent(string nodeId)
    {
        lock (_lock) return _buckets.TryGetValue(nodeId, out var b) ? b.Spent : 0;
    }

    private Bucket Refill(NodeConnection node, long now)
    {
        var rate = RateFor(node);
        if (!_buckets.TryGetValue(node.Id, out var bucket))
        {
            bucket = _buckets[node.Id] = new Bucket { Tokens = rate, LastRefill = now };
        }

        var elapsed = _clock.GetElapsedTime(bucket.LastRefill, now).TotalSeconds;
        bucket.Tokens = Math.Min(rate, bucket.Tokens + elapsed * rate);
        bucket.LastRefill = now;
        return bucket;
    }
}
