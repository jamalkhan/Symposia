using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Symposia.Network.Node;

/// <summary>
/// Executes cold-start step 1 ("the node generates its keypair and registers
/// on-chain", per Requirements/BlobStorage/metadata-architecture.md) and keeps
/// the node's on-chain endpoint in sync with <see cref="NodeNetworkOptions.AdvertisedEndpoint"/>
/// (issue #129 FR-C3). Runs in the background and retries until it succeeds,
/// so a node started before its gateway still comes up and serves.
/// </summary>
public sealed class NodeRegistrationHostedService(
    NodeIdentity identity,
    INodeRegistryClient registry,
    IOptions<NodeNetworkOptions> options,
    ILogger<NodeRegistrationHostedService> logger) : BackgroundService
{
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BlockchainGatewayUrl) || string.IsNullOrWhiteSpace(o.NodeRegistryAddress))
        {
            logger.LogInformation("Blockchain gateway not configured; skipping on-chain node registration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(o.AdvertisedEndpoint))
        {
            logger.LogWarning("No AdvertisedEndpoint configured; skipping on-chain node registration, so peers cannot discover this node.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RegisterOnceAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "On-chain node registration failed; retrying in {Delay}.", RetryDelay);
            }

            await Task.Delay(RetryDelay, stoppingToken);
        }
    }

    public async Task RegisterOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        var address = identity.NodeId;
        var status = await registry.GetStatusAsync(address, ct);

        if (status.Registered && status.Endpoint == o.AdvertisedEndpoint)
        {
            logger.LogInformation("Node {Address} is already registered on-chain at {Endpoint}.", address, status.Endpoint);
            return;
        }

        var signature = identity.SignRegistration(o.AdvertisedEndpoint, status.Nonce, o.NodeRegistryAddress, o.ChainId);
        await registry.RegisterAsync(address, o.AdvertisedEndpoint, status.Nonce, signature, ct);
        logger.LogInformation("Registered node {Address} on-chain at {Endpoint}.", address, o.AdvertisedEndpoint);
    }
}
