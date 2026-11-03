using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// Keeps the gateway's storage nodes in step with the on-chain NodeRegistry (issue #135).
/// Each cycle lists registered nodes through the Blockchain Gateway, asks each one where its
/// storage gRPC API lives (<see cref="NodeServices.Path"/>), and hands the result to
/// <see cref="NodeRegistry.SyncFromRegistry"/>. Nodes without a storage service (for example
/// compute-only nodes) are skipped. Identity is verified later by the Probe NodeId check.
/// </summary>
public sealed class RegistryNodeSync(
    NodeRegistry nodes,
    INodeRegistryClient registry,
    IHttpClientFactory httpFactory,
    IOptions<GatewayOptions> options,
    ILogger<RegistryNodeSync> logger) : BackgroundService
{
    public const string HttpClientName = "node-services";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.RegistryRefreshSeconds)));
        do
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not refresh storage nodes from the registry; keeping the current list.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SyncOnceAsync(CancellationToken ct)
    {
        var members = await registry.ListNodesAsync(ct);
        logger.LogDebug("Registry lists {Count} node(s); resolving storage services.", members.Count);

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(members, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (member, token) =>
        {
            var grpc = await StorageGrpcUrlAsync(member, token);
            if (grpc is not null)
            {
                lock (found)
                {
                    found[grpc] = member.Node;
                }
            }
        });

        nodes.SyncFromRegistry(found);
        logger.LogDebug("Registry sync: {Storage} storage node(s) of {Count} registered.", found.Count, members.Count);
    }

    private async Task<string?> StorageGrpcUrlAsync(RegisteredNode member, CancellationToken ct)
    {
        var endpoint = NodeEndpoint.Normalize(member.Endpoint);
        if (endpoint is null)
        {
            logger.LogDebug("Skipping node {Node}: registered endpoint {Endpoint} is not usable.", member.Node, member.Endpoint);
            return null;
        }

        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            var services = await http.GetFromJsonAsync<NodeServices>(endpoint + NodeServices.Path, ct);
            if (services is null || !string.Equals(services.Node, member.Node, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Node {Node} at {Endpoint} described itself as {Answered}; skipping.", member.Node, endpoint, services?.Node);
                return null;
            }

            if (services.Storage?.Grpc is not { Length: > 0 } grpc)
            {
                logger.LogDebug("Skipping node {Node}: it offers no storage service.", member.Node);
                return null;
            }

            return grpc;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // 404 = an endpoint without the services document (compute-only, or an older node).
            logger.LogDebug("Skipping node {Node}: services lookup at {Endpoint} failed ({Error}).", member.Node, endpoint, ex.Message);
            return null;
        }
    }
}
