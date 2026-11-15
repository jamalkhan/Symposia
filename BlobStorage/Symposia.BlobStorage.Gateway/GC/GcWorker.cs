using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.GC;

/// <summary>
/// Executes GC's physical deletions (#16): drains gc_queue by priority (high: replicas on slashed or terminated
/// nodes; medium: soft-delete purges and plain deletes; low: stale replicas and orphans, FR8.2), with a share of
/// every batch reserved for the low tier so it is deferred under backlog but never starved (FR8.3). Every entry
/// passes <see cref="SafetyInvariantGate"/> first (FR7), and deletions to each node are rate-capped so GC
/// never competes with client I/O (FR8.1). Each confirmed replica deletion is recorded (FR2.3); when a blob's
/// last replica goes, its versions are marked physically deleted and billing stops then (FR9).
/// </summary>
public sealed class GcWorker : BackgroundService
{
    private const int BatchSize = 50;

    private readonly GatewayMetadataStore _store;
    private readonly INodeRegistry _nodes;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<GcWorker> _logger;
    private readonly SafetyInvariantGate _gate;
    private readonly Dictionary<string, (long Second, int Count)> _rate = new(StringComparer.OrdinalIgnoreCase);

    public GcWorker(
        GatewayMetadataStore store,
        INodeRegistry nodes,
        IOptions<GatewayOptions> options,
        ILogger<GcWorker> logger,
        SafetyInvariantGate gate)
    {
        _store = store;
        _nodes = nodes;
        _options = options;
        _logger = logger;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GcWorker encountered an unexpected error.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_options.Value.GcRetryIntervalSeconds), stoppingToken);
        }
    }

    /// <summary>One batch; internal so tests can drive GC deterministically. Returns how many replicas were deleted.</summary>
    internal async Task<int> DrainBatchAsync(CancellationToken ct)
    {
        var o = _options.Value;
        var pending = _store.DueDeletions(BatchSize, TimeSpan.FromSeconds(o.GcMinAgeSeconds), o.Gc.LowPriorityReserve);
        if (pending.Count == 0) return 0;

        _logger.LogInformation("GcWorker processing {Count} pending node deletions.", pending.Count);
        var deleted = 0;
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();

            switch (_gate.Check(entry.Cid, entry.NodeUrl, entry.Category))
            {
                case GcVerdict.Refused:
                    _store.RemoveFromGcQueue(entry.Id);
                    _store.ConfirmReplicaDeleted(entry.Cid); // nothing more will go for this entry
                    continue;
                case GcVerdict.Deferred:
                    _store.IncrementGcAttempt(entry.Id);
                    continue;
            }

            var node = _nodes.All.FirstOrDefault(n =>
                n.Id.Equals(entry.NodeUrl, StringComparison.OrdinalIgnoreCase));

            if (node is null || !node.IsReachable)
            {
                // Node unknown or offline — leave in queue for later.
                _store.IncrementGcAttempt(entry.Id);
                continue;
            }

            if (!TakeRate(node.Id, o.Gc.DeletesPerSecondPerNode))
            {
                continue; // this node's GC budget for the second is spent; the entry waits for the next batch
            }

            try
            {
                await node.Client.DeleteBlobAsync(
                    new DeleteBlobRequest { Cid = entry.Cid },
                    cancellationToken: ct);

                _logger.LogDebug("GC: deleted blob {Cid} from node {Url} ({Category}).", entry.Cid, entry.NodeUrl, entry.Category);
                _store.RemoveFromGcQueue(entry.Id);
                _store.ConfirmReplicaDeleted(entry.Cid);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "GC: attempt {Attempt} to delete {Cid} from {Url} failed: {Message}",
                    entry.Attempts + 1, entry.Cid, entry.NodeUrl, ex.Message);
                _store.IncrementGcAttempt(entry.Id);
            }
        }

        return deleted;
    }

    private bool TakeRate(string nodeId, int perSecond)
    {
        var second = Environment.TickCount64 / 1000;
        var (at, count) = _rate.GetValueOrDefault(nodeId);
        if (at != second)
        {
            (at, count) = (second, 0);
        }

        if (count >= Math.Max(1, perSecond))
        {
            return false;
        }

        _rate[nodeId] = (at, count + 1);
        return true;
    }
}
