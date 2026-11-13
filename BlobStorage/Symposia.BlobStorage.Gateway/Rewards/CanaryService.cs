using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.Network.Node;
using Symposia.Network.Node.Rewards;

namespace Symposia.BlobStorage.Gateway.Rewards;

/// <summary>
/// Keeps this gateway's probe canary (#225 FR3.2) in storage: written through the normal quorum path into
/// <see cref="ProbeChallenge.CanaryBucket"/>, which has no bucket row, so no tenant can list or read it. It is
/// recreated only when its metadata is gone, never because a read failed: that would hide a broken read path.
/// </summary>
public sealed class CanaryService(IServiceScopeFactory scopes, GatewayMetadataStore store, NodeIdentity identity, ILogger<CanaryService> logger) : BackgroundService
{
    private static readonly TimeSpan Present = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Missing = TimeSpan.FromMinutes(1);

    public string Key => ProbeChallenge.CanaryKey(identity.NodeId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            var ok = false;
            try
            {
                ok = await EnsureAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Probe canary: couldn't write it, retrying: {Reason}", ex.Message);
            }

            try
            {
                await Task.Delay(ok ? Present : Missing, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Writes the canary when its metadata is missing; true when it exists afterwards.</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        if (store.GetObject(ProbeChallenge.CanaryBucket, Key) is not null)
        {
            return true;
        }

        using var scope = scopes.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<Quorum.QuorumWriter>();
        using var body = new MemoryStream(ProbeChallenge.Canary(identity.NodeId));
        var (record, _) = await writer.WriteObjectAsync("", ProbeChallenge.CanaryBucket, Key, "application/octet-stream", body, ct, ProbeChallenge.CanaryBytes);
        if (record is null)
        {
            logger.LogWarning("Probe canary: write quorum not reached; retrying in a minute.");
            return false;
        }

        logger.LogInformation("Probe canary written ({Cid}).", record.Cid);
        return true;
    }
}
