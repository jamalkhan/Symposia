using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Symposia.Network.Node;

/// <summary>
/// Builds this node's peer table (issue #129 FR-C1/FR-C2/FR-C4): every
/// discovery interval it lists registry members via the Gateway, adds any
/// configured seed peers, and handshakes with each. If the Gateway is
/// unreachable the previous table is kept and the cycle retried later.
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
        if (string.IsNullOrWhiteSpace(o.BlockchainGatewayUrl) || string.IsNullOrWhiteSpace(o.NodeRegistryAddress))
        {
            logger.LogInformation("Blockchain gateway not configured; peer discovery disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, o.DiscoveryIntervalSeconds)));
        do
        {
            await RunCycleAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        var o = options.Value;
        var self = identity.NodeId;

        IReadOnlyList<RegisteredNode> members;
        try
        {
            members = await registry.ListNodesAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not list registry members from the gateway; keeping last known peer table.");
            return;
        }

        var candidates = members
            .Where(m => !PeerVerifier.SameAddress(m.Node, self) && !string.IsNullOrWhiteSpace(m.Endpoint))
            .Select(m => (m.Endpoint, Expected: (string?)m.Node))
            .ToList();

        foreach (var seed in o.SeedPeers.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            if (!SameEndpoint(seed, o.AdvertisedEndpoint) && !candidates.Any(c => SameEndpoint(c.Endpoint, seed)))
            {
                candidates.Add((seed, null));
            }
        }

        foreach (var (endpoint, expected) in candidates)
        {
            PeerEntry entry;
            try
            {
                entry = await verifier.VerifyAsync(endpoint, expected, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
            {
                logger.LogWarning(ex, "Registry check for peer {Endpoint} failed; will retry next cycle.", endpoint);
                continue;
            }

            table.Upsert(entry);
            if (entry.Status == PeerStatus.Recognized)
            {
                logger.LogInformation("Recognized peer {Address} at {Endpoint}.", entry.Address, endpoint);
            }
            else
            {
                logger.LogWarning("Peer at {Endpoint} not recognized: {Reason} (address {Address}).", endpoint, entry.Reason, entry.Address);
            }
        }
    }

    private static bool SameEndpoint(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
