using System.Globalization;
using System.Numerics;
using System.Text;
using Symposia.NodeHost.ServiceCatalog;

namespace Symposia.NodeHost.Ledger;

public enum LedgerCategory
{
    Stake,
    Reward,
    Penalty,
}

/// <summary>
/// One ledger row. <see cref="AmountWei"/> is signed from the operator's point of view: stake posted
/// leaves the wallet (negative), rewards arrive (positive), slashes are lost (negative). Informational
/// rows (an unstake request, a zero settlement, a ban) carry 0.
/// </summary>
public sealed record LedgerEntry(
    ulong Block,
    long LogIndex,
    string TxHash,
    long Timestamp,
    ulong? Epoch,
    string Service,
    LedgerCategory Category,
    string Type,
    BigInteger AmountWei,
    string Detail)
{
    public string Amount => LedgerService.FormatSym(AmountWei);
}

public sealed record ServiceTotals(string Service, string Stake, string Earned, string Slashed, string ReserveHeld);

public sealed record LedgerSnapshot(bool Available, string? Reason, IReadOnlyList<LedgerEntry> Entries, IReadOnlyList<ServiceTotals> Totals, ulong ScannedToBlock);

/// <summary>
/// The Ledger tab (issue #161): every on-chain stake, reward and penalty event for this node, per Node
/// Service. Scans <c>eth_getLogs</c> in chunks from the registry's deployment block, then only new blocks
/// on later loads (an in-memory cursor; one scan at a time).
/// </summary>
/// <param name="confirmations">Blocks behind the head to stop at, so a reorg can't leave orphaned rows behind.</param>
/// <param name="storeFor">Where the current network's ledger is persisted (#163); null = memory only.</param>
public sealed class LedgerService(
    Func<ILedgerLogSource?> sourceFactory, string node, Func<string?> unavailableReason, ulong confirmations = 0, Func<LedgerStore?>? storeFor = null)
{
    /// <summary>On load, a cursor whose block hash no longer matches the chain rewinds this far.</summary>
    public const ulong ReorgRewind = 1_000;

    public const ulong ChunkSize = 5_000;

    /// <summary>Labels for <c>RewardDistributor.SettlementReason</c>, by ordinal (pinned by LedgerEnumTests).</summary>
    public static readonly string[] SettlementReasons = ["None", "Paid", "Excluded", "Ineligible", "Stage penalty", "Zero amount"];

    /// <summary>Labels for <c>INodeRegistry.NodeStatus</c>, by ordinal (pinned by LedgerEnumTests).</summary>
    public static readonly string[] Statuses = ["Unregistered", "Pending verification", "Active", "Suspended", "Banned", "Deregistering"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<LedgerEntry> _entries = [];
    private ILedgerLogSource? _source;
    private LedgerStore? _store;
    private ulong? _cursor;
    private (ulong Block, IReadOnlyDictionary<int, BigInteger> Reserves)? _reserves;

    public async Task<LedgerSnapshot> SnapshotAsync(CancellationToken ct)
    {
        if (unavailableReason() is { } reason)
        {
            return new LedgerSnapshot(false, reason, [], [], 0);
        }

        await _gate.WaitAsync(ct);
        try
        {
            var source = sourceFactory();
            if (source is null)
            {
                return new LedgerSnapshot(false, "The chain or the reward contracts aren't known yet.", [], [], 0);
            }

            if (!ReferenceEquals(source, _source))
            {
                // A different network (or redeploy): start over.
                _source = source;
                _entries.Clear();
                _cursor = null;
                _reserves = null;
                _store = storeFor?.Invoke();
                await LoadAsync(source, ct);
            }

            await ScanAsync(source, ct);
            return new LedgerSnapshot(true, null, _entries.ToList(), await TotalsAsync(source, ct), _cursor ?? 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resumes from the persisted ledger, rewinding if the chain no longer has the cursor's block.</summary>
    private async Task LoadAsync(ILedgerLogSource source, CancellationToken ct)
    {
        if (_store is null)
        {
            return;
        }

        var (entries, cursor) = _store.Load();
        if (cursor is null)
        {
            return;
        }

        string? hash = null;
        try
        {
            hash = await source.BlockHashAsync(cursor.ScannedTo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown block (e.g. a reset local chain): treat as a mismatch.
        }

        if (string.Equals(hash, cursor.BlockHash, StringComparison.OrdinalIgnoreCase))
        {
            _entries.AddRange(entries);
            _cursor = cursor.ScannedTo;
            return;
        }

        var keepTo = cursor.ScannedTo > ReorgRewind ? cursor.ScannedTo - ReorgRewind : 0;
        var kept = keepTo == 0 ? [] : entries.Where(e => e.Block <= keepTo).ToList();
        _entries.AddRange(kept);
        _cursor = keepTo == 0 ? null : keepTo;
        _store.Replace(kept, new LedgerStore.Cursor(keepTo, keepTo == 0 ? "" : await source.BlockHashAsync(keepTo, ct)));
    }

    private async Task ScanAsync(ILedgerLogSource source, CancellationToken ct)
    {
        var head = await source.LatestBlockAsync(ct);
        if (head < confirmations)
        {
            return;
        }

        var latest = head - confirmations;
        var from = _cursor is { } c ? c + 1 : await source.StartBlockAsync(ct);
        while (from <= latest)
        {
            var to = Math.Min(latest, from + ChunkSize - 1);
            var logs = await source.LogsAsync(node, from, to, ct);
            // A chunk lands together with its cursor: a failure part-way leaves nothing behind to duplicate.
            var chunk = new List<LedgerEntry>();
            (ulong Block, long Ts)? last = null; // only the current block's timestamp is kept
            foreach (var log in logs.OrderBy(l => l.Block).ThenBy(l => l.LogIndex))
            {
                if (last?.Block != log.Block)
                {
                    last = (log.Block, await source.BlockTimestampAsync(log.Block, ct));
                }

                var ts = last.Value.Ts;

                if (Map(log, ts) is { } entry)
                {
                    chunk.Add(entry);
                }
            }

            _store?.Append(chunk, new LedgerStore.Cursor(to, await source.BlockHashAsync(to, ct)));
            _entries.AddRange(chunk);
            _cursor = to;
            from = to + 1;
        }
    }

    /// <summary>Turns one event into a ledger row; null for events that duplicate another row.</summary>
    public static LedgerEntry? Map(LedgerLog log, long timestamp)
    {
        var service = ServiceName(log.Service);
        LedgerEntry Row(LedgerCategory category, string type, BigInteger amount, string detail, ulong? epoch = null) =>
            new(log.Block, log.LogIndex, log.TxHash, timestamp, epoch, service, category, type, amount, detail);

        switch (log)
        {
            case ServiceRegisteredLog r:
                return Row(LedgerCategory.Stake, "Service registered", -r.Stake, $"Staked {FormatSym(r.Stake)} SYM");
            case StakeChangedLog s when s.Reason == 0:
                return null; // the ServiceRegistered row covers it
            case StakeChangedLog s when s.Reason == 1:
                return Row(LedgerCategory.Stake, "Top-up", -(s.Current - s.Previous), $"Stake {FormatSym(s.Previous)} → {FormatSym(s.Current)} SYM");
            case StakeChangedLog s when s.Reason == 2:
                return Row(LedgerCategory.Stake, "Unstake withdrawn", s.Previous - s.Current, $"Stake {FormatSym(s.Previous)} → {FormatSym(s.Current)} SYM");
            case StakeChangedLog s:
                return Row(LedgerCategory.Penalty, "Slashed", -(s.Previous - s.Current), $"Stake {FormatSym(s.Previous)} → {FormatSym(s.Current)} SYM");
            case UnstakeRequestedLog u:
                var until = DateTimeOffset.FromUnixTimeSeconds((long)u.CooldownEnd).UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
                return Row(LedgerCategory.Stake, "Unstake requested", BigInteger.Zero, $"{FormatSym(u.Amount)} SYM after the cooldown ends {until}");
            case StatusChangedLog st when st.Current == 4:
                return Row(LedgerCategory.Penalty, "Banned", BigInteger.Zero, $"{Status(st.Previous)} → Banned");
            case StatusChangedLog:
                return null; // routine status changes aren't money movements
            case ServiceSettledLog p when p.Reason == 1:
                return Row(LedgerCategory.Reward, "Earned", p.Amount,
                    p.Held > 0 ? $"Held in reserve: {FormatSym(p.Held)} SYM (heartbeat compliance at or below 90%)"
                    : $"Paid now: {FormatSym(p.Paid)} SYM", p.Epoch);
            case ServiceSettledLog p:
                return Row(LedgerCategory.Reward, "No reward", BigInteger.Zero, SettlementReason(p.Reason), p.Epoch);
            case ReserveClaimedLog c:
                return Row(LedgerCategory.Reward, "Reserve claimed", BigInteger.Zero, $"{FormatSym(c.Amount)} SYM released from reserve (already counted when earned)");
            default:
                return null;
        }
    }

    private async Task<IReadOnlyList<ServiceTotals>> TotalsAsync(ILedgerLogSource source, CancellationToken ct)
    {
        var totals = new List<ServiceTotals>();
        foreach (var group in _entries.GroupBy(e => e.Service))
        {
            var stake = BigInteger.Zero;
            var earned = BigInteger.Zero;
            var slashed = BigInteger.Zero;
            foreach (var e in group)
            {
                switch (e.Category)
                {
                    case LedgerCategory.Stake:
                        stake -= e.AmountWei; // posting stake is negative in the wallet, positive in stake
                        break;
                    case LedgerCategory.Reward:
                        earned += e.AmountWei;
                        break;
                    case LedgerCategory.Penalty:
                        slashed -= e.AmountWei;
                        stake += e.AmountWei;
                        break;
                }
            }

            var id = NodeServiceCatalog.All.FirstOrDefault(s => s.Name == group.Key)?.Id;
            var reserve = id is { } sid ? await ReserveAsync(source, sid, ct) : BigInteger.Zero;
            totals.Add(new ServiceTotals(group.Key, FormatSym(stake), FormatSym(earned), FormatSym(slashed), FormatSym(reserve)));
        }

        return totals;
    }

    /// <summary>Reserve reads are cached until the scan moves to a new block.</summary>
    private async Task<BigInteger> ReserveAsync(ILedgerLogSource source, int service, CancellationToken ct)
    {
        var block = _cursor ?? 0;
        if (_reserves is not { } cached || cached.Block != block)
        {
            cached = (block, new Dictionary<int, BigInteger>());
            _reserves = cached;
        }

        var map = (Dictionary<int, BigInteger>)cached.Reserves;
        if (!map.TryGetValue(service, out var value))
        {
            value = await source.ReserveAsync(node, service, ct);
            map[service] = value;
        }

        return value;
    }

    public static IEnumerable<LedgerEntry> Filter(IEnumerable<LedgerEntry> entries, string? category, string? service, int? limit)
    {
        var filtered = entries
            .Where(e => string.IsNullOrEmpty(category) || e.Category.ToString().Equals(category, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrEmpty(service) || e.Service.Equals(service, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Block).ThenByDescending(e => e.LogIndex);
        return limit is > 0 ? filtered.Take(limit.Value) : filtered;
    }

    public static string ToCsv(IEnumerable<LedgerEntry> entries)
    {
        var csv = new StringBuilder("time_utc,block,tx_hash,epoch,service,category,type,amount_sym,detail\n");
        foreach (var e in entries)
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(e.Timestamp).UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
            csv.Append(string.Join(',', time, e.Block, e.TxHash, e.Epoch?.ToString(CultureInfo.InvariantCulture) ?? "", Quote(e.Service),
                e.Category, Quote(e.Type), e.Amount, Quote(e.Detail))).Append('\n');
        }

        return csv.ToString();
    }

    private static string Quote(string value) => value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    /// <summary>Exact decimal SYM (18 decimals), trailing zeros trimmed: -1500000000000000000 → "-1.5".</summary>
    public static string FormatSym(BigInteger wei)
    {
        var negative = wei.Sign < 0;
        var abs = BigInteger.Abs(wei);
        var whole = BigInteger.DivRem(abs, BigInteger.Pow(10, 18), out var frac);
        var text = whole.ToString(CultureInfo.InvariantCulture);
        if (!frac.IsZero)
        {
            text += "." + frac.ToString(CultureInfo.InvariantCulture).PadLeft(18, '0').TrimEnd('0');
        }

        return negative ? "-" + text : text;
    }

    private static string ServiceName(int id) => NodeServiceCatalog.All.FirstOrDefault(s => s.Id == id)?.Name ?? $"Service {id}";

    private static string Status(int s) => s >= 0 && s < Statuses.Length ? Statuses[s] : $"Status {s}";

    private static string SettlementReason(int r) => r >= 0 && r < SettlementReasons.Length ? SettlementReasons[r] : $"Reason {r}";
}
