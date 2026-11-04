namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>A storage node as a source describes it: stable identity plus where to reach it now.</summary>
public sealed record StorageNodeDescriptor(string Id, string Url);

/// <summary>
/// Where the gateway's storage nodes come from (issue #137). <see cref="NodeSourceSyncService"/>
/// feeds the result to <see cref="NodeRegistry.Sync"/> every refresh. This is also the extension
/// point for placement (#8) and read selection (#11) to learn about nodes.
/// </summary>
public interface IStorageNodeSource
{
    TimeSpan RefreshInterval { get; }

    Task<IReadOnlyList<StorageNodeDescriptor>> DiscoverAsync(CancellationToken ct);
}

/// <summary>Configured nodes plus nodes added through /admin/nodes; a static node's identity is its URL.</summary>
public sealed class StaticNodeSource(Microsoft.Extensions.Options.IOptions<GatewayOptions> options, Metadata.GatewayMetadataStore store) : IStorageNodeSource
{
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Max(1, options.Value.ProbeIntervalSeconds));

    public Task<IReadOnlyList<StorageNodeDescriptor>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StorageNodeDescriptor>>(options.Value.Nodes
            .Concat(store.GetPersistedNodes())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(url => new StorageNodeDescriptor(url, url))
            .ToList());
}

/// <summary>Runs the configured <see cref="IStorageNodeSource"/> and applies its result to the node registry.</summary>
public sealed class NodeSourceSyncService(
    IStorageNodeSource source, NodeRegistry nodes, ILogger<NodeSourceSyncService> logger) : BackgroundService
{
    /// <summary>Loads the node list before the gateway starts serving, so the first requests after a restart aren't 503s.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        first.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            nodes.Sync(await source.DiscoverAsync(first.Token));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Initial storage node discovery did not finish ({Error}); continuing in the background.", ex.Message);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(source.RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                nodes.Sync(await source.DiscoverAsync(stoppingToken));
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not refresh storage nodes; keeping the current list.");
            }
        }
    }
}
