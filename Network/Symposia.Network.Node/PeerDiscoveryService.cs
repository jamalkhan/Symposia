using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Symposia.Network.Node;

/// <summary>
/// Builds this node's peer table (issue #129 FR-C1/FR-C2/FR-C4): every
/// discovery interval it lists registry members via the Gateway, adds any
/// configured seed peers, and handshakes with each (bounded in parallel). If
/// the Gateway is unreachable the previous table is kept and the cycle
/// retried later; no single failure stops discovery.
/// </summary>
public sealed class PeerDiscoveryService(
    NodeIdentity identity,
    INodeRegistryClient registry,
    PeerVerifier verifier,
    PeerTable table,
    IOptions<NodeNetworkOptions> options,
    ILogger<PeerDiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.EnableDiscovery)
        {
            logger.LogInformation("Peer discovery disabled for this host (EnableDiscovery=false).");
            return;
        }

        if (string.IsNullOrWhiteSpace(o.BlockchainGatewayUrl) || string.IsNullOrWhiteSpace(o.NodeRegistryAddress))
        {
            logger.LogInformation("Blockchain gateway not configured; peer discovery disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, o.DiscoveryIntervalSeconds)));
        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Peer discovery cycle failed; retrying next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        var o = options.Value;
        var self = identity.NodeId;
        var selfEndpoint = NodeEndpoint.Normalize(o.AdvertisedEndpoint);

        var started = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<RegisteredNode> members;
        try
        {
            members = await registry.ListNodesAsync(ct);
            logger.LogDebug("Discovery cycle: registry lists {Count} node(s).", members.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not list registry members from the gateway; keeping last known peer table.");
            return;
        }

        var candidates = members
            .Where(m => !PeerVerifier.SameAddress(m.Node, self) && !string.IsNullOrWhiteSpace(m.Endpoint))
            .Select(m => (Endpoint: NodeEndpoint.Normalize(m.Endpoint) ?? m.Endpoint, Expected: (string?)m.Node))
            .ToList();

        foreach (var seed in o.SeedPeers.Select(NodeEndpoint.Normalize).OfType<string>().Distinct())
        {
            if (seed != selfEndpoint && !candidates.Any(c => c.Endpoint == seed))
            {
                candidates.Add((seed, null));
            }
        }

        if (candidates.Count == 0)
        {
            logger.LogDebug("Discovery cycle: no other nodes to verify (registry has only this node, or none, and no seed peers).");
        }

        var previous = table.Snapshot().ToDictionary(p => p.Endpoint, p => p);
        var results = new System.Collections.Concurrent.ConcurrentBag<PeerEntry>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.MaxConcurrentHandshakes), CancellationToken = ct },
            async (candidate, token) =>
            {
                try
                {
                    logger.LogTrace("Handshaking with {Endpoint} (expected {Expected}).", candidate.Endpoint, candidate.Expected ?? "any");
                    var entry = await verifier.VerifyAsync(candidate.Endpoint, candidate.Expected, token);
                    results.Add(entry);
                    Log(entry, previous.GetValueOrDefault(entry.Endpoint));
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Verifying peer {Endpoint} failed; will retry next cycle.", candidate.Endpoint);
                }
            });

        foreach (var gone in previous.Keys.Except(results.Select(r => r.Endpoint)))
        {
            logger.LogInformation("Peer at {Endpoint} is no longer a discovery candidate.", gone);
        }

        table.Replace(results);
        logger.LogDebug("Discovery cycle finished in {ElapsedMs} ms: {Recognized} recognized, {Unrecognized} not recognized.",
            started.ElapsedMilliseconds, results.Count(r => r.Status == PeerStatus.Recognized), results.Count(r => r.Status != PeerStatus.Recognized));
    }

    /// <summary>Changes in a peer's state are logged at Information/Warning; repeats each cycle only at Debug.</summary>
    private void Log(PeerEntry entry, PeerEntry? before)
    {
        var changed = before is null || before.Status != entry.Status || before.Reason != entry.Reason || before.Address != entry.Address;
        if (entry.Status == PeerStatus.Recognized)
        {
            logger.Log(changed ? LogLevel.Information : LogLevel.Debug, "Recognized peer {Address} at {Endpoint}.", entry.Address, entry.Endpoint);
        }
        else
        {
            logger.Log(changed ? LogLevel.Warning : LogLevel.Debug, "Peer at {Endpoint} not recognized: {Reason} (address {Address}).", entry.Endpoint, entry.Reason, entry.Address);
        }
    }
}
