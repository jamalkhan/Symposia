using System.Numerics;
using Symposia.Network.Node.Staking;

namespace Symposia.NodeHost.Components;

/// <summary>
/// This host's self-staking on a LocalDev network (#200): one loop per network, shared by every role,
/// so a host's services are staked one at a time from one account and funded together. Roles ask for
/// their service when they start; the loop retries every 30 s until each is Active (or can't be), and
/// never faults a component.
/// </summary>
public sealed class LocalDevStaking : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<byte, long> _wanted = [];
    private readonly HashSet<byte> _settled = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private readonly NodeServiceStaker _staker;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private Task? _loop;

    private LocalDevStaking(NetworkInfo network, HostContext context)
    {
        Network = network;
        _logger = context.CreateLogger("host", "Symposia.NodeHost.LocalDevStaking");
        var identity = new Symposia.Network.Node.NodeIdentity(Microsoft.Extensions.Options.Options.Create(
            new Symposia.Network.Node.NodeNetworkOptions { NodeIdentityKeyPath = context.Paths.NodeKey }));
        identity.EnsureLoadedOrGenerated();
        _staker = new NodeServiceStaker(identity, network.ChainId, network.RpcUrl!, network.StakingRegistryAddress!,
            new DevGatewayStakeFunding(identity, network.ChainId, new Uri(network.GatewayUrl), _http), _logger);
    }

    public NetworkInfo Network { get; }

    /// <summary>The staking for <paramref name="network"/>, or null when it isn't a LocalDev network with a staking registry.</summary>
    public static LocalDevStaking? For(NetworkInfo network, HostContext context) =>
        network.ChainId == DevFunding.LocalDevChainId && !string.IsNullOrEmpty(network.StakingRegistryAddress) && !string.IsNullOrEmpty(network.RpcUrl)
            ? new LocalDevStaking(network, context)
            : null;

    /// <summary>Stakes <paramref name="service"/> (if not already) for <paramref name="capacity"/> units.</summary>
    public void Request(byte service, long capacity)
    {
        lock (_wanted)
        {
            _wanted[service] = capacity;
            _settled.Remove(service);
            _loop ??= Task.Run(() => RunAsync(_stop.Token));
        }

        _signal.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var lastProblem = "";
        while (!ct.IsCancellationRequested)
        {
            List<(byte, BigInteger)> pending;
            lock (_wanted)
            {
                pending = [.. _wanted.Where(w => !_settled.Contains(w.Key)).Select(w => (w.Key, (BigInteger)w.Value))];
            }

            if (pending.Count > 0)
            {
                try
                {
                    foreach (var (service, outcome) in await _staker.EnsureStakedAsync(pending, ct))
                    {
                        if (outcome is StakeOutcome.Retry)
                        {
                            continue;
                        }

                        lock (_wanted)
                        {
                            _settled.Add(service);
                        }

                        if (outcome == StakeOutcome.Active)
                            _logger.LogInformation("Node Service {Service} is staked and Active on this LocalDev network.", service);
                        else
                            _logger.LogWarning("Not staking Node Service {Service}: {Outcome} (no dev funding on this network, the grant can't cover it, or it's suspended or banned).",
                                service, outcome);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (ex.Message != lastProblem)
                    {
                        _logger.LogWarning("Staking failed; retrying: {Reason}", ex.Message);
                        lastProblem = ex.Message;
                    }
                }
            }

            try
            {
                // Wake early when a role asks for another service; otherwise retry on the interval.
                await _signal.WaitAsync(pending.Count > 0 ? RetryInterval : Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _http.Dispose();
    }
}
