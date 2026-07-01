namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// Lightweight hosted service that periodically checks for nodes that have reconnected
/// (JustReconnected flag set by NodeRegistry.ProbeAsync) and delegates to NodeReconciler.
/// Runs every 30 seconds — matches the probe interval so reconciliation happens within one
/// probe cycle of a node coming back online.
/// </summary>
public sealed class ReconcilePollerService : BackgroundService
{
    private readonly NodeReconciler _reconciler;
    private readonly ILogger<ReconcilePollerService> _logger;

    public ReconcilePollerService(NodeReconciler reconciler, ILogger<ReconcilePollerService> logger)
    {
        _reconciler = reconciler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial startup reconciliation — check all known nodes (they may already be healthy).
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        await _reconciler.CheckForReconnectsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            try
            {
                await _reconciler.CheckForReconnectsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReconcilePollerService encountered an unexpected error.");
            }
        }
    }
}
