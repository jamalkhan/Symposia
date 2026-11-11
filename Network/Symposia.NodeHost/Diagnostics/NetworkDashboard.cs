using System.Numerics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Symposia.NodeHost.Components;

namespace Symposia.NodeHost.Diagnostics;

/// <summary>What the chain's telemetry checkpoints say about storage nodes over the uptime window.</summary>
/// <param name="MeanUptimeBps">Per node, the mean checkpointed uptime across the sampled epochs it has records for.</param>
/// <param name="Latest">Per node, used and available bytes from its most recent record.</param>
public sealed record StorageHistory(
    IReadOnlyDictionary<string, double> MeanUptimeBps,
    IReadOnlyDictionary<string, (ulong Used, ulong Available)> Latest,
    int EpochsSampled,
    DateTimeOffset At);

public interface IStorageHistorySource
{
    /// <summary>Null when the network has no telemetry contracts (or they can't be read).</summary>
    Task<StorageHistory?> LoadAsync(IReadOnlyCollection<string> nodes, CancellationToken ct);
}

public sealed record VersionShare(string Version, int Count, double Percent);

public sealed record StoragePopulation(int Registered, int Available, int ReadOnly, int Unhealthy);

public sealed record StorageNetworkSummary(
    DateTimeOffset AsOf,
    StoragePopulation Population,
    long UsedBytes,
    long AvailableBytes,
    int FromCheckpoint,
    int UsedUnknown,
    double? MeanUptimePercent,
    int UptimeNodes,
    int UptimeNodesWithoutData,
    int UptimeWindowDays,
    DateTimeOffset? UptimeAsOf,
    IReadOnlyList<VersionShare> Versions,
    string? Error);

/// <summary>
/// The Network Dashboard's Symposia Storage Network section (issue #198): network-wide storage
/// totals over every registered node, from the same crawl as the Network tab plus on-chain
/// telemetry for 30-day uptime and for nodes that don't answer. Cached for 60 s (uptime 1 h).
/// </summary>
public sealed class NetworkDashboard(NetworkOverview overview, IStorageHistorySource history, IMemoryCache cache, ILogger<NetworkDashboard> logger)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HistoryCacheFor = TimeSpan.FromHours(1);
    public const int UptimeWindowDays = 30;
    public const int TopVersions = 5;

    /// <summary>A history load (nodes × sampled epochs of view calls) is abandoned after this long.</summary>
    public static readonly TimeSpan HistoryTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _historyGate = new(1, 1);

    public async Task<StorageNetworkSummary> StorageAsync(CancellationToken ct)
    {
        if (cache.TryGetValue("dashboard|storage", out StorageNetworkSummary? hit) && hit is not null)
        {
            return hit;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue("dashboard|storage", out hit) && hit is not null)
            {
                return hit;
            }

            var crawl = await overview.CrawlAsync();
            var past = await HistoryAsync(StorageRows(crawl.Rows).Select(r => r.Node).ToList());
            var summary = Summarize(crawl, past);
            cache.Set("dashboard|storage", summary, CacheFor);
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The chain history, cached for an hour. Loaded by one caller at a time, bounded by
    /// <see cref="HistoryTimeout"/>, and independent of any request's cancellation, so a slow RPC
    /// delays at most one refresh and a failed load is cached briefly instead of retried every time.
    /// </summary>
    private async Task<StorageHistory?> HistoryAsync(IReadOnlyList<string> nodes)
    {
        if (nodes.Count == 0)
        {
            return null;
        }

        var key = "dashboard|history|" + string.Join(',', nodes.Order(StringComparer.OrdinalIgnoreCase));
        if (cache.TryGetValue(key, out StorageHistory? past))
        {
            return past;
        }

        if (!await _historyGate.WaitAsync(0))
        {
            // Another refresh is loading it; show totals now and uptime once that load lands.
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(HistoryTimeout);
            try
            {
                past = await history.LoadAsync(nodes, timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Network dashboard: telemetry history unavailable ({Error})", ex.Message);
            }

            cache.Set(key, past, past is null ? CacheFor : HistoryCacheFor);
            return past;
        }
        finally
        {
            _historyGate.Release();
        }
    }

    /// <summary>
    /// Registered nodes that are (or may be) storage nodes: any that report a storage role or serve
    /// storage, plus unreachable ones, whose role can't be confirmed and which count as unreachable.
    /// </summary>
    internal static IEnumerable<NetworkNodeRow> StorageRows(IEnumerable<NetworkNodeRow> rows) =>
        rows.Where(r => r.Registered && (r.Storage.Configured || r.Services?.Storage is not null || r.Storage.Level == HealthLevel.Unknown));

    internal static StorageNetworkSummary Summarize(NetworkCrawl crawl, StorageHistory? past)
    {
        var rows = StorageRows(crawl.Rows).ToList();
        int available = 0, readOnly = 0, unhealthy = 0, fromCheckpoint = 0, usedUnknown = 0;
        long used = 0, free = 0;
        foreach (var r in rows)
        {
            var s = r.Storage;
            // Precedence: unreachable > unhealthy > read-only > available; each node counted once.
            if (s.Level == HealthLevel.Unknown)
            {
                unhealthy++;
                if (past?.Latest.TryGetValue(r.Node.ToLowerInvariant(), out var last) == true)
                {
                    fromCheckpoint++;
                    used += (long)last.Used;
                    free += (long)last.Available;
                }
                else
                {
                    usedUnknown++;
                }

                continue;
            }

            if (s.Level == HealthLevel.Bad || s.Mode == nameof(Symposia.BlobStorage.StorageNode.Storage.StorageMode.Critical))
            {
                unhealthy++;
            }
            else if (s.ReadOnly == true)
            {
                readOnly++;
            }
            else
            {
                available++;
            }

            free += s.FreeBytes ?? 0;
            if (s.UsedBytes is { } u)
            {
                used += u;
            }
            else
            {
                // Nodes predating #199 don't report used bytes.
                usedUnknown++;
            }
        }

        var uptimes = past is null ? [] : rows.Select(r => r.Node.ToLowerInvariant())
            .Where(n => past.MeanUptimeBps.ContainsKey(n)).Select(n => past.MeanUptimeBps[n]).ToList();
        var versions = rows
            .GroupBy(r => r.Services is { ProtocolVersion: > 0 } svc ? $"protocol v{svc.ProtocolVersion}" : "unknown")
            .Select(g => (Name: g.Key, Count: g.Count(), Order: g.First().Services?.ProtocolVersion ?? 0))
            .OrderByDescending(v => v.Count).ThenByDescending(v => v.Name == "unknown" ? -1 : v.Order)
            .Take(TopVersions)
            .Select(v => new VersionShare(v.Name, v.Count, rows.Count == 0 ? 0 : Math.Round(100.0 * v.Count / rows.Count, 1)))
            .ToList();

        return new StorageNetworkSummary(
            crawl.At,
            new StoragePopulation(rows.Count, available, readOnly, unhealthy),
            used,
            free,
            fromCheckpoint,
            usedUnknown,
            uptimes.Count == 0 ? null : Math.Round(uptimes.Average() / 100, 1),
            uptimes.Count,
            rows.Count - uptimes.Count,
            UptimeWindowDays,
            past?.At,
            versions,
            crawl.Error);
    }
}

/// <summary>
/// Reads storage nodes' per-epoch records from <c>TelemetryCheckpointRegistry.getEpochMetricRecord</c>
/// (no event carries uptime, so these are view calls). Over the 30-day window it samples at most
/// <see cref="MaxEpochs"/> completed epochs, evenly spaced and always including the latest, so short
/// LocalDev epochs don't cost thousands of calls. Verifier-attested uptime is preferred; self-reported
/// is used when an epoch had no attestations.
/// </summary>
public sealed class ChainStorageHistorySource(ComponentSupervisor supervisor, ILogger<ChainStorageHistorySource> logger) : IStorageHistorySource
{
    public const int MaxEpochs = 30;
    private const int Concurrency = 8;

    public async Task<StorageHistory?> LoadAsync(IReadOnlyCollection<string> nodes, CancellationToken ct)
    {
        var network = supervisor.Context.Network;
        if (network?.RpcUrl is null || string.IsNullOrEmpty(network.TelemetryRegistryAddress) || string.IsNullOrEmpty(network.EpochControllerAddress))
        {
            return null;
        }

        var chain = new Symposia.Network.Node.Telemetry.Committee.NethereumTelemetryChain(new()
        {
            RpcUrl = network.RpcUrl,
            TelemetryRegistryAddress = network.TelemetryRegistryAddress,
            EpochControllerAddress = network.EpochControllerAddress,
        });
        var now = await chain.NowAsync(ct);
        var schedule = await chain.ScheduleAsync(ct);
        if (now < schedule.GenesisUnixSeconds)
        {
            return new StorageHistory(new Dictionary<string, double>(), new Dictionary<string, (ulong, ulong)>(), 0, DateTimeOffset.UtcNow);
        }

        var current = (long)schedule.EpochAt(now);
        var windowStart = now - (long)TimeSpan.FromDays(NetworkDashboard.UptimeWindowDays).TotalSeconds;
        var first = windowStart <= schedule.GenesisUnixSeconds ? 0 : (long)schedule.EpochAt(windowStart);
        var epochs = SampleEpochs(first, current - 1, MaxEpochs);

        var web3 = new Nethereum.Web3.Web3(network.RpcUrl);
        var query = web3.Eth.GetContractQueryHandler<GetEpochMetricRecordFunction>();
        var calls = nodes.SelectMany(n => epochs.Select(e => (Node: n.ToLowerInvariant(), Epoch: e))).ToList();
        var results = new (string Node, long Epoch, EpochMetricRecord? Record, bool Read)[calls.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, calls.Count), new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
            async (i, token) =>
            {
                var (node, epoch) = calls[i];
                try
                {
                    var output = await query.QueryDeserializingToObjectAsync<EpochMetricRecordOutput>(
                        new GetEpochMetricRecordFunction { Node = node, Epoch = epoch }, network.TelemetryRegistryAddress).WaitAsync(token);
                    results[i] = (node, epoch, output?.Record is { Exists: true } r ? r : null, true);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // One failed read leaves that epoch out rather than losing everyone's history.
                    logger.LogDebug("getEpochMetricRecord({Node}, {Epoch}) failed: {Error}", node, epoch, ex.Message);
                    results[i] = (node, epoch, null, false);
                }
            });
        logger.LogDebug("Network dashboard: read {Calls} epoch record(s) for {Nodes} node(s) over {Epochs} epoch(s)", calls.Count, nodes.Count, epochs.Count);

        var uptime = new Dictionary<string, double>();
        var latest = new Dictionary<string, (ulong, ulong)>();
        foreach (var g in results.GroupBy(r => r.Node))
        {
            var recorded = g.Where(r => r.Record is not null).ToList();
            if (recorded.Count == 0)
            {
                continue;
            }

            // From a node's first recorded epoch on, an epoch it has no record for was an epoch it was
            // down, so it counts as 0 rather than being skipped. Epochs that failed to read are left out.
            var since = recorded.Min(r => r.Epoch);
            uptime[g.Key] = g.Where(r => r.Read && r.Epoch >= since)
                .Average(r => r.Record is null ? 0.0 : r.Record.AttestationCount > 0 ? r.Record.VerifierAttested.UptimeBps : r.Record.SelfReported.UptimeBps);
            var last = recorded.MaxBy(r => r.Epoch).Record!;
            latest[g.Key] = (last.UsedBytes, last.AvailableBytes);
        }

        return new StorageHistory(uptime, latest, epochs.Count, DateTimeOffset.UtcNow);
    }

    /// <summary>At most <paramref name="max"/> epochs from first..last inclusive, evenly spaced, always including last.</summary>
    internal static IReadOnlyList<long> SampleEpochs(long first, long last, int max)
    {
        if (last < first || last < 0)
        {
            return [];
        }

        first = Math.Max(0, first);
        var count = last - first + 1;
        if (count <= max)
        {
            return Enumerable.Range(0, (int)count).Select(i => first + i).ToList();
        }

        var step = (double)(count - 1) / (max - 1);
        return Enumerable.Range(0, max).Select(i => last - (long)Math.Round(i * step)).Distinct().Order().ToList();
    }

    [Function("getEpochMetricRecord")]
    public sealed class GetEpochMetricRecordFunction : FunctionMessage
    {
        [Parameter("address", "node", 1)] public string Node { get; set; } = "";

        [Parameter("uint256", "epoch", 2)] public BigInteger Epoch { get; set; }
    }

    [FunctionOutput]
    public sealed class EpochMetricRecordOutput : IFunctionOutputDTO
    {
        [Parameter("tuple", "", 1, structTypeName: "AggregatedMetricRecord")] public EpochMetricRecord Record { get; set; } = new();
    }

    /// <summary>ABI mirror of <c>TelemetryCheckpointRegistry.AggregatedMetricRecord</c>.</summary>
    [Struct("AggregatedMetricRecord")]
    public sealed class EpochMetricRecord
    {
        [Parameter("tuple", "selfReported", 1, structTypeName: "MetricValues")] public Symposia.Blockchain.Contracts.MetricValuesInput SelfReported { get; set; } = new();
        [Parameter("tuple", "verifierAttested", 2, structTypeName: "MetricValues")] public Symposia.Blockchain.Contracts.MetricValuesInput VerifierAttested { get; set; } = new();
        [Parameter("uint32", "heartbeatsReceived", 3)] public uint HeartbeatsReceived { get; set; }
        [Parameter("uint32", "heartbeatsExpected", 4)] public uint HeartbeatsExpected { get; set; }
        [Parameter("uint16", "heartbeatComplianceBps", 5)] public ushort HeartbeatComplianceBps { get; set; }
        [Parameter("uint16", "attestationCount", 6)] public ushort AttestationCount { get; set; }
        [Parameter("uint16", "firstCheckpoint", 7)] public ushort FirstCheckpoint { get; set; }
        [Parameter("uint16", "lastCheckpoint", 8)] public ushort LastCheckpoint { get; set; }
        [Parameter("bool", "eligible", 9)] public bool Eligible { get; set; }
        [Parameter("bool", "divergenceFlag", 10)] public bool DivergenceFlag { get; set; }
        [Parameter("bool", "exists", 11)] public bool Exists { get; set; }
        [Parameter("uint64", "availableBytes", 12)] public ulong AvailableBytes { get; set; }
        [Parameter("uint64", "usedBytes", 13)] public ulong UsedBytes { get; set; }
    }
}
