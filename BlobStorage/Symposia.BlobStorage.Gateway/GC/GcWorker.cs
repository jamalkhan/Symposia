using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.GC;

/// <summary>
/// Background service that drains the gc_queue, retrying blob deletions on storage nodes that
/// were unreachable when DeleteObject was originally called.
///
/// Safety invariant: a blob is never physically deleted from a node if any other object in the
/// metadata still references its CID (content-addressed dedup), checked at retry time.
///
/// See Requirements/BlobStorage/garbage-collection.md#categories-of-garbage (soft-deleted blobs).
/// </summary>
public sealed class GcWorker : BackgroundService
{
    private const int BatchSize = 50;

    private readonly GatewayMetadataStore _store;
    private readonly INodeRegistry _nodes;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<GcWorker> _logger;

    public GcWorker(
        GatewayMetadataStore store,
        INodeRegistry nodes,
        IOptions<GatewayOptions> options,
        ILogger<GcWorker> logger)
    {
        _store = store;
        _nodes = nodes;
        _options = options;
        _logger = logger;
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

    private async Task DrainBatchAsync(CancellationToken ct)
    {
        var pending = _store.GetPendingDeletions(BatchSize);
        if (pending.Count == 0) return;

        _logger.LogInformation("GcWorker processing {Count} pending node deletions.", pending.Count);

        foreach (var (id, cid, nodeUrl, attempts) in pending)
        {
            ct.ThrowIfCancellationRequested();

            // Skip deletion if another object still references this CID (dedup safety).
            if (_store.IsCidReferenced(cid))
            {
                _logger.LogDebug("CID {Cid} is still referenced; removing GC entry {Id}.", cid, id);
                _store.RemoveFromGcQueue(id);
                continue;
            }

            var node = _nodes.All.FirstOrDefault(n =>
                n.Url.Equals(nodeUrl, StringComparison.OrdinalIgnoreCase));

            if (node is null || !node.IsHealthy)
            {
                // Node unknown or offline — leave in queue for later.
                _store.IncrementGcAttempt(id);
                continue;
            }

            try
            {
                await node.Client.DeleteBlobAsync(
                    new DeleteBlobRequest { Cid = cid },
                    cancellationToken: ct);

                _logger.LogDebug("GC: deleted blob {Cid} from node {Url}.", cid, nodeUrl);
                _store.RemoveFromGcQueue(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "GC: attempt {Attempt} to delete {Cid} from {Url} failed: {Message}",
                    attempts + 1, cid, nodeUrl, ex.Message);
                _store.IncrementGcAttempt(id);
            }
        }
    }
}
