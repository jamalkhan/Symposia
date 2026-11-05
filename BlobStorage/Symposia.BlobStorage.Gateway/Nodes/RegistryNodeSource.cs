using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// Storage nodes from the on-chain NodeRegistry (issues #135, #137): lists registered nodes through
/// the Blockchain Gateway and asks each where its storage gRPC API lives
/// (<see cref="NodeServices.Path"/>). A node's identity is its registry address. Nodes without a
/// storage service (compute-only) are skipped, and <see cref="INetworkPolicy"/> decides whether and how
/// each of the rest is used (#145); identity is verified later by the Probe NodeId check.
/// </summary>
public sealed class RegistryNodeSource(
    INodeRegistryClient registry,
    IHttpClientFactory httpFactory,
    INetworkPolicy policy,
    IOptions<GatewayOptions> options,
    ILogger<RegistryNodeSource> logger) : IStorageNodeSource
{
    public const string HttpClientName = "node-services";

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Max(1, options.Value.RegistryRefreshSeconds));

    public async Task<IReadOnlyList<StorageNodeDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        var members = await registry.ListNodesAsync(ct);
        logger.LogDebug("Registry lists {Count} node(s); resolving storage services.", members.Count);

        var found = new Dictionary<string, StorageNodeDescriptor>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(members, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (member, token) =>
        {
            var descriptor = await DescribeAsync(member, token);
            if (descriptor is not null)
            {
                lock (found)
                {
                    found[descriptor.RefusedBecause is null ? descriptor.Url : "refused:" + descriptor.Id] = descriptor;
                }
            }
        });

        logger.LogDebug("Registry sync: {Storage} usable storage node(s) of {Count} registered.",
            found.Values.Count(d => d.RefusedBecause is null), members.Count);
        return found.Values.ToList();
    }

    // The services document was fetched from this endpoint, so loopback here means the node is local.
    private static bool IsLocal(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && u.IsLoopback;

    private async Task<StorageNodeDescriptor?> DescribeAsync(RegisteredNode member, CancellationToken ct)
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

            if (services.Storage is null)
            {
                logger.LogDebug("Skipping node {Node}: it offers no storage service.", member.Node);
                return null;
            }

            // TC-60: a node on another machine advertising loopback is unreachable from here; the
            // operator needs to set its advertised endpoint to a LAN address.
            var grpc = services.Storage.TlsUrl ?? services.Storage.PlaintextUrl;
            if (Uri.TryCreate(grpc, UriKind.Absolute, out var grpcUri) && grpcUri.IsLoopback && !IsLocal(endpoint))
            {
                logger.LogWarning("Node {Node} advertises loopback storage address {Grpc}; if it runs on another machine, set its advertised endpoint to a LAN address.",
                    member.Node, grpc);
            }

            // Network policy (#145): required features, minimum version, transport and TLS pinning.
            var admitted = policy.AdmitStorageNode(member.Node, services);
            foreach (var warning in admitted.Warnings)
            {
                logger.LogWarning("Storage node {Node}: {Warning}. Upgrade that host before then.", member.Node, warning);
            }

            if (admitted.Node is null)
            {
                logger.LogError("Not using storage node {Node}: {Reason}.", member.Node, admitted.RefusedBecause);
                return StorageNodeDescriptor.Refused(member.Node, admitted.RefusedBecause!);
            }

            return admitted.Node;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // 404 = an endpoint without the services document (compute-only, or an older node).
            logger.LogDebug("Skipping node {Node}: services lookup at {Endpoint} failed ({Error}).", member.Node, endpoint, ex.Message);
            return null;
        }
    }
}
