using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>
/// Storage nodes from the on-chain NodeRegistry (issues #135, #137): lists registered nodes through
/// the Blockchain Gateway and asks each where its storage gRPC API lives
/// (<see cref="NodeServices.Path"/>). A node's identity is its registry address. Nodes without a
/// storage service (compute-only) are skipped; identity is verified later by the Probe NodeId check.
/// </summary>
public sealed class RegistryNodeSource(
    INodeRegistryClient registry,
    IHttpClientFactory httpFactory,
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
                    found[descriptor.Url] = descriptor;
                }
            }
        });

        logger.LogDebug("Registry sync: {Storage} storage node(s) of {Count} registered.", found.Count, members.Count);
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

            if (services.Storage?.Grpc is not { Length: > 0 } grpc)
            {
                logger.LogDebug("Skipping node {Node}: it offers no storage service.", member.Node);
                return null;
            }

            // TC-60: a node on another machine advertising loopback is unreachable from here; the
            // operator needs to set its advertised endpoint to a LAN address.
            if (Uri.TryCreate(grpc, UriKind.Absolute, out var grpcUri) && grpcUri.IsLoopback && !IsLocal(endpoint))
            {
                logger.LogWarning("Node {Node} advertises loopback storage address {Grpc}; if it runs on another machine, set its advertised endpoint to a LAN address.",
                    member.Node, grpc);
            }

            // TLS (#142): pin only a fingerprint the node's registry key vouches for; refuse cleartext if required.
            var o = options.Value;
            if (grpc.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var fingerprint = services.Storage.TlsFingerprint;
                if (fingerprint is null || !StorageTlsProof.Verify(o.ChainId, o.NodeRegistryAddress, member.Node, fingerprint, services.Storage.TlsProof))
                {
                    logger.LogWarning("Node {Node} offers TLS storage without a valid certificate proof; not using it.", member.Node);
                    return null;
                }

                return new StorageNodeDescriptor(member.Node, grpc, fingerprint.ToLowerInvariant());
            }

            if (o.RequireStorageTls)
            {
                logger.LogWarning("Node {Node} offers only cleartext storage gRPC ({Grpc}) and TLS is required; not using it. Upgrade that host.",
                    member.Node, grpc);
                return null;
            }

            return new StorageNodeDescriptor(member.Node, grpc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // 404 = an endpoint without the services document (compute-only, or an older node).
            logger.LogDebug("Skipping node {Node}: services lookup at {Endpoint} failed ({Error}).", member.Node, endpoint, ex.Message);
            return null;
        }
    }
}
