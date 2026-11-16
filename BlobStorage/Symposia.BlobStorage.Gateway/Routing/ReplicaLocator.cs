using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Routing;

/// <summary>
/// Finds holders of a CID when neither the metadata index nor the routing cache leads to a readable replica
/// (#12 FR2.6, FR4.4, AC3/AC4): first this gateway's peers (they may have written it, or have a warmer cache),
/// then nodes that can prove it against their on-chain epoch root. Either way the read doesn't fail just because
/// gossip is late or this gateway's cache is cold. Whatever is found is learned into the cache.
/// </summary>
public sealed class ReplicaLocator(
    RoutingCache cache,
    INodeRegistry nodes,
    EpochRootDisputes chain,
    IHttpClientFactory http,
    Health.GatewayMetrics metrics,
    IOptions<GatewayOptions> options,
    ILogger<ReplicaLocator> logger)
{
    public const string HttpClientName = "peer-gateways";

    /// <summary>Holders of <paramref name="cid"/> outside <paramref name="tried"/>, healthiest first; empty when none is found.</summary>
    public async Task<IReadOnlyList<NodeConnection>> LocateAsync(string cid, IReadOnlySet<string> tried, CancellationToken ct)
    {
        var fromPeers = await AskPeersAsync(cid, ct);
        var found = S3.ReplicaReader.Order(nodes, fromPeers).Where(n => !tried.Contains(n.Id)).ToList();
        if (found.Count > 0)
        {
            metrics.RoutingLookups.WithLabels("peer").Inc();
            found.ForEach(n => cache.Learn(n.Id, cid));
            return found;
        }

        if (options.Value.Routing.ChainFallback && chain.Enabled)
        {
            found = await AskChainAsync(cid, tried, ct);
            if (found.Count > 0)
            {
                metrics.RoutingLookups.WithLabels("chain").Inc();
                found.ForEach(n => cache.Learn(n.Id, cid));
                return found;
            }
        }

        metrics.RoutingLookups.WithLabels("none").Inc();
        return [];
    }

    private async Task<IReadOnlyList<string>> AskPeersAsync(string cid, CancellationToken ct)
    {
        var routing = options.Value.Routing;
        if (routing.PeerGateways.Length == 0 || routing.PeerToken.Length == 0)
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(1, routing.PeerTimeoutMilliseconds));
        var client = http.CreateClient(HttpClientName);
        var answers = await Task.WhenAll(routing.PeerGateways.Select(async peer =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{peer.TrimEnd('/')}{PeerRoutingEndpoints.Path}/{cid}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", routing.PeerToken);
                using var response = await client.SendAsync(request, timeout.Token);
                return response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<PeerRoutingEndpoints.Answer>(timeout.Token) is { } answer
                    ? answer.Nodes : [];
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                logger.LogDebug("Peer gateway {Peer} couldn't resolve {Cid}: {Message}", peer, cid, ex.Message);
                return [];
            }
        }));
        return [.. answers.SelectMany(a => a).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<List<NodeConnection>> AskChainAsync(string cid, IReadOnlySet<string> tried, CancellationToken ct)
    {
        var candidates = nodes.Healthy.Where(n => n.ExpectedNodeId is not null && !tried.Contains(n.Id)).ToList();
        var attested = new System.Collections.Concurrent.ConcurrentBag<NodeConnection>();
        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.Routing.ChainFallbackParallelism), CancellationToken = ct },
            async (node, token) =>
            {
                if ((await chain.CheckAsync(node, cid, token)).Outcome == Attestation.Attested)
                {
                    attested.Add(node);
                }
            });
        return S3.ReplicaReader.Order(nodes, attested.Select(n => n.Id)).ToList();
    }
}
