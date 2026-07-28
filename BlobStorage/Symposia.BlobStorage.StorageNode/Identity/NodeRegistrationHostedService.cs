using Microsoft.Extensions.Options;

namespace Symposia.BlobStorage.StorageNode.Identity;

/// <summary>
/// Executes cold-start step 1 ("the node generates its keypair and registers
/// on-chain", per Requirements/BlobStorage/metadata-architecture.md) against
/// the Bootstrap Chain Gateway (issue #110). Runs in the background so a
/// node without gateway connectivity yet (e.g. local dev with no chain
/// configured) still starts and serves its other endpoints; registration is
/// idempotent, so it is safe to retry on every startup.
/// </summary>
public sealed class NodeRegistrationHostedService(
    NodeIdentity identity,
    NodeRegistrationClient registrationClient,
    IOptions<StorageNodeOptions> options,
    ILogger<NodeRegistrationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var storageNodeOptions = options.Value;
        if (string.IsNullOrWhiteSpace(storageNodeOptions.BlockchainGatewayUrl) ||
            string.IsNullOrWhiteSpace(storageNodeOptions.NodeRegistryAddress))
        {
            logger.LogInformation("Blockchain gateway not configured; skipping on-chain node registration.");
            return;
        }

        try
        {
            var address = identity.NodeId;

            if (await registrationClient.IsRegisteredAsync(address, stoppingToken))
            {
                logger.LogInformation("Node {Address} is already registered on-chain.", address);
                return;
            }

            var signature = identity.SignRegistration(storageNodeOptions.NodeRegistryAddress, storageNodeOptions.ChainId);
            await registrationClient.RegisterAsync(address, signature, stoppingToken);
            logger.LogInformation("Registered node {Address} on-chain.", address);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "On-chain node registration failed; will not block node startup.");
        }
    }
}
