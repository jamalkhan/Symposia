using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.Health;

/// <summary>
/// Checks the metadata index connection in the background (#1 FR-24), so <c>/ready</c> reads a cached
/// flag instead of doing I/O inside its 500 ms budget.
/// </summary>
public sealed class MetadataIndexMonitor(GatewayMetadataStore store, ILogger<MetadataIndexMonitor> logger) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    private volatile bool _connected;

    public bool IsConnected => _connected;

    /// <summary>Seconds since the index last answered; 0 while connected.</summary>
    public double LagSeconds => _connected ? 0 : (DateTimeOffset.UtcNow - _lastOk).TotalSeconds;

    private DateTimeOffset _lastOk = DateTimeOffset.UtcNow;

    /// <summary>One check, also run synchronously at startup so the first <c>/ready</c> is accurate.</summary>
    public void Check()
    {
        var ok = store.Ping();
        if (ok)
        {
            _lastOk = DateTimeOffset.UtcNow;
        }
        else if (_connected)
        {
            logger.LogError("Metadata index is not answering; reporting not ready.");
        }

        _connected = ok;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            Check();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
