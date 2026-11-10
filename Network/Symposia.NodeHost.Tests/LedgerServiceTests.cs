using System.Numerics;
using System.Text.Json;
using Symposia.NodeHost.Ledger;

namespace Symposia.NodeHost.Tests;

/// <summary>Ledger tab mapping, scanning, totals and export (issue #161; QA LED-01..LED-09).</summary>
public sealed class LedgerServiceTests
{
    private const string Node = "0x70997970C51812dc3A010C7d01b50e0d17dc79C8";
    private static readonly BigInteger Sym = BigInteger.Pow(10, 18);

    private sealed class FakeSource : ILedgerLogSource
    {
        public ulong Latest { get; set; }

        public List<LedgerLog> Logs { get; } = [];

        public List<(ulong From, ulong To)> Calls { get; } = [];

        public Task<ulong> LatestBlockAsync(CancellationToken ct) => Task.FromResult(Latest);

        public Task<ulong> StartBlockAsync(CancellationToken ct) => Task.FromResult(0UL);

        public Task<IReadOnlyList<LedgerLog>> LogsAsync(string node, ulong from, ulong to, CancellationToken ct)
        {
            Calls.Add((from, to));
            return Task.FromResult<IReadOnlyList<LedgerLog>>(Logs.Where(l => l.Block >= from && l.Block <= to).Reverse().ToList());
        }

        public Task<long> BlockTimestampAsync(ulong block, CancellationToken ct) => Task.FromResult(1_700_000_000L + (long)block);

        public string HashSalt { get; set; } = "a";

        public int FailHashes { get; set; }

        public Task<string> BlockHashAsync(ulong block, CancellationToken ct) => FailHashes-- > 0
            ? throw new HttpRequestException("429 Too Many Requests")
            : Task.FromResult(block > Latest ? "" : $"0x{HashSalt}{block}"); // "" = the chain has no such block

        public Task<BigInteger> ReserveAsync(string node, int service, CancellationToken ct) => Task.FromResult(service == 0 ? 3 * Sym : BigInteger.Zero);
    }

    private static (LedgerService Ledger, FakeSource Source) Create(string? unavailable = null)
    {
        var source = new FakeSource();
        return (new LedgerService(() => source, Node, () => unavailable), source);
    }

    private static void SeedLifecycle(FakeSource s)
    {
        s.Logs.Add(new ServiceRegisteredLog(1, 0, "0xa", 0, 100 * Sym));
        s.Logs.Add(new StakeChangedLog(1, 1, "0xa", 0, 0, 100 * Sym, 0));
        s.Logs.Add(new StatusChangedLog(1, 2, "0xa", 0, 0, 1));
        s.Logs.Add(new StakeChangedLog(2, 0, "0xb", 0, 100 * Sym, 150 * Sym, 1));
        s.Logs.Add(new ServiceSettledLog(3, 0, "0xc", 0, 0, 1, 12 * Sym, 12 * Sym, 0));
        s.Logs.Add(new ServiceSettledLog(3, 1, "0xc", 8, 0, 3, 0, 0, 0));
        s.Logs.Add(new StakeChangedLog(4, 0, "0xd", 0, 150 * Sym, 140 * Sym, 3));
        s.Logs.Add(new StatusChangedLog(4, 1, "0xd", 0, 2, 4));
        s.Logs.Add(new UnstakeRequestedLog(5, 0, "0xe", 0, 40 * Sym, 1_800_000_000));
        s.Logs.Add(new ReserveClaimedLog(6, 0, "0xf", 0, 3 * Sym));
    }

    [Fact]
    public async Task MapsEveryEventToItsCategory_InChainOrder()
    {
        var (ledger, source) = Create();
        source.Latest = 10;
        SeedLifecycle(source);

        var snap = await ledger.SnapshotAsync(default);

        Assert.True(snap.Available);
        Assert.Equal(
            ["Service registered", "Top-up", "Earned", "No reward", "Slashed", "Banned", "Unstake requested", "Reserve claimed"],
            snap.Entries.Select(e => e.Type));
        var reg = snap.Entries[0];
        Assert.Equal((LedgerCategory.Stake, "Storage", "-100"), (reg.Category, reg.Service, reg.Amount));
        Assert.Equal("-50", snap.Entries[1].Amount);
        var earned = snap.Entries[2];
        Assert.Equal((LedgerCategory.Reward, "+12", 0UL), (earned.Category, "+" + earned.Amount, earned.Epoch!.Value));
        Assert.Equal(("Blob Gateway", "Ineligible"), (snap.Entries[3].Service, snap.Entries[3].Detail));
        Assert.Equal((LedgerCategory.Penalty, "-10"), (snap.Entries[4].Category, snap.Entries[4].Amount));
        Assert.Equal(LedgerCategory.Penalty, snap.Entries[5].Category);
        Assert.Equal("0", snap.Entries[6].Amount);
        Assert.Equal(1_700_000_001L, reg.Timestamp);
    }

    [Fact]
    public async Task Totals_StakeEarnedSlashedAndReserve_PerService()
    {
        var (ledger, source) = Create();
        source.Latest = 10;
        SeedLifecycle(source);

        var snap = await ledger.SnapshotAsync(default);
        var storage = snap.Totals.Single(t => t.Service == "Storage");

        Assert.Equal(("140", "12", "10", "3"), (storage.Stake, storage.Earned, storage.Slashed, storage.ReserveHeld));
        Assert.Equal("0", snap.Totals.Single(t => t.Service == "Blob Gateway").Earned);
    }

    [Fact]
    public async Task SecondLoad_ScansOnlyNewBlocks()
    {
        var (ledger, source) = Create();
        source.Latest = 10;
        SeedLifecycle(source);
        await ledger.SnapshotAsync(default);

        source.Latest = 12;
        source.Logs.Add(new ServiceSettledLog(12, 0, "0x10", 0, 1, 1, 5 * Sym, 5 * Sym, 0));
        var snap = await ledger.SnapshotAsync(default);

        Assert.Equal((11UL, 12UL), source.Calls[^1]);
        Assert.Equal(9, snap.Entries.Count);
        Assert.Equal(12UL, snap.ScannedToBlock);
    }

    [Fact]
    public async Task LongRanges_AreScannedInChunks()
    {
        var (ledger, source) = Create();
        source.Latest = 12_000;

        await ledger.SnapshotAsync(default);

        Assert.Equal([(0UL, 4_999UL), (5_000UL, 9_999UL), (10_000UL, 12_000UL)], source.Calls);
    }

    [Fact]
    public async Task Unavailable_ExplainsWhy()
    {
        var (ledger, source) = Create("This network has no rewards contracts yet.");

        var snap = await ledger.SnapshotAsync(default);

        Assert.False(snap.Available);
        Assert.Equal("This network has no rewards contracts yet.", snap.Reason);
        Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task Filter_ByCategoryAndService_NewestFirst()
    {
        var (ledger, source) = Create();
        source.Latest = 10;
        SeedLifecycle(source);
        var snap = await ledger.SnapshotAsync(default);

        var penalties = LedgerService.Filter(snap.Entries, "penalty", null, null).ToList();
        Assert.Equal(["Banned", "Slashed"], penalties.Select(e => e.Type));
        Assert.All(LedgerService.Filter(snap.Entries, null, "Blob Gateway", null), e => Assert.Equal("Blob Gateway", e.Service));
        Assert.Single(LedgerService.Filter(snap.Entries, null, null, 1));
    }

    [Fact]
    public async Task Csv_HasHeaderAndExactAmounts()
    {
        var (ledger, source) = Create();
        source.Latest = 10;
        source.Logs.Add(new ServiceSettledLog(3, 0, "0xc", 0, 7, 1, Sym + 1, Sym + 1, 0));
        var snap = await ledger.SnapshotAsync(default);

        var csv = LedgerService.ToCsv(snap.Entries).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("time_utc,block,tx_hash,epoch,service,category,type,amount_sym,detail", csv[0]);
        Assert.Contains(",3,0xc,7,Storage,Reward,Earned,1.000000000000000001,", csv[1]);
    }

    [Fact]
    public async Task Restart_ResumesFromThePersistedCursor()
    {
        using var dir = new TempDir();
        var source = new FakeSource { Latest = 10 };
        SeedLifecycle(source);
        var first = new LedgerService(() => source, Node, () => null, 0, () => new LedgerStore(dir.Path));
        await first.SnapshotAsync(default);

        source.Calls.Clear();
        source.Latest = 12;
        var restarted = new LedgerService(() => source, Node, () => null, 0, () => new LedgerStore(dir.Path));
        var snap = await restarted.SnapshotAsync(default);

        Assert.Equal([(11UL, 12UL)], source.Calls);
        Assert.Equal(8, snap.Entries.Count);
    }

    // Issue #182: a rate-limited read while resuming is not a reorg, so the persisted ledger is kept.
    [Fact]
    public async Task Restart_WhenTheCursorReadFails_KeepsThePersistedLedger()
    {
        using var dir = new TempDir();
        var source = new FakeSource { Latest = 10 };
        SeedLifecycle(source);
        await new LedgerService(() => source, Node, () => null, 0, () => new LedgerStore(dir.Path)).SnapshotAsync(default);

        source.FailHashes = 1;
        source.Calls.Clear();
        var restarted = new LedgerService(() => source, Node, () => null, 0, () => new LedgerStore(dir.Path));
        await Assert.ThrowsAsync<HttpRequestException>(() => restarted.SnapshotAsync(default));
        var snap = await restarted.SnapshotAsync(default);

        Assert.Empty(source.Calls); // resumed at block 10: nothing rescanned
        Assert.Equal(8, snap.Entries.Count);
    }

    [Fact]
    public async Task Restart_AfterAReorg_RewindsAndRescans()
    {
        using var dir = new TempDir();
        var source = new FakeSource { Latest = 2_000 };
        source.Logs.Add(new ServiceRegisteredLog(5, 0, "0xa", 0, Sym));
        source.Logs.Add(new ServiceRegisteredLog(1_500, 0, "0xb", 8, Sym));
        await new LedgerService(() => source, Node, () => null, 0, () => new LedgerStore(dir.Path)).SnapshotAsync(default);

        source.HashSalt = "b"; // every block hash changed: the persisted cursor block is gone
        source.Calls.Clear();
        var snap = await new LedgerService(() => source, Node, () => null, 0, () => new LedgerStore(dir.Path)).SnapshotAsync(default);

        Assert.Equal(1_001UL, source.Calls[0].From);
        Assert.Equal(2, snap.Entries.Count);
        Assert.Single(snap.Entries, e => e.Block == 1_500);
    }

    // #182 review: a chain reset to one shorter than the rewind point starts over instead of
    // persisting the old chain's rows under a hashless cursor.
    [Fact]
    public async Task Restart_OnAShorterResetChain_StartsOver()
    {
        using var dir = new TempDir();
        var source = new FakeSource { Latest = 2_000 };
        source.Logs.Add(new ServiceRegisteredLog(1_500, 0, "0xb", 8, Sym));
        await new LedgerService(() => source, Node, () => null, 0, () => new LedgerStore(dir.Path)).SnapshotAsync(default);

        source.Logs.Clear();
        source.Latest = 50; // a fresh local chain: neither the cursor nor the rewind point exists
        var snap = await new LedgerService(() => source, Node, () => null, 0, () => new LedgerStore(dir.Path)).SnapshotAsync(default);

        Assert.Empty(snap.Entries);
        Assert.Equal(50UL, snap.ScannedToBlock);
    }

    [Fact]
    public void Store_IgnoresATornLastLineAndRowsPastTheCursor()
    {
        using var dir = new TempDir();
        var store = new LedgerStore(dir.Path);
        var row = new LedgerEntry(3, 0, "0xc", 1, 0, "Storage", LedgerCategory.Reward, "Earned", Sym, "");
        store.Append([row], new LedgerStore.Cursor(3, "0x3"));
        File.AppendAllText(Path.Combine(dir.Path, "entries.jsonl"), JsonSerializer.Serialize(row with { Block = 9 }) + "\n{\"Block\":");

        var (entries, cursor) = store.Load();

        Assert.Equal(3UL, cursor!.ScannedTo);
        Assert.Equal(Sym, Assert.Single(entries).AmountWei);
    }

    [Theory]
    [InlineData("1500000000000000000", "1.5")]
    [InlineData("-1500000000000000000", "-1.5")]
    [InlineData("0", "0")]
    [InlineData("1", "0.000000000000000001")]
    public void FormatSym_IsExact(string wei, string expected) =>
        Assert.Equal(expected, LedgerService.FormatSym(BigInteger.Parse(wei)));
}

/// <summary>Review fixes (Frank #1, #2): partial-chunk failures and confirmation depth.</summary>
public sealed class LedgerScanSafetyTests
{
    private sealed class FlakySource : ILedgerLogSource
    {
        public bool FailTimestamps { get; set; }

        public ulong Latest { get; set; } = 10;

        public Task<ulong> LatestBlockAsync(CancellationToken ct) => Task.FromResult(Latest);

        public Task<ulong> StartBlockAsync(CancellationToken ct) => Task.FromResult(0UL);

        public Task<IReadOnlyList<LedgerLog>> LogsAsync(string node, ulong from, ulong to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LedgerLog>>(new LedgerLog[]
            {
                new ServiceRegisteredLog(1, 0, "0xa", 0, BigInteger.One),
                new ServiceRegisteredLog(2, 0, "0xb", 8, BigInteger.One),
            }.Where(l => l.Block >= from && l.Block <= to).ToList());

        public Task<long> BlockTimestampAsync(ulong block, CancellationToken ct) =>
            FailTimestamps && block == 2 ? throw new HttpRequestException("timeout") : Task.FromResult(1L);

        public Task<string> BlockHashAsync(ulong block, CancellationToken ct) => Task.FromResult($"0x{block}");

        public Task<BigInteger> ReserveAsync(string node, int service, CancellationToken ct) => Task.FromResult(BigInteger.Zero);
    }

    [Fact]
    public async Task FailureMidChunk_DoesNotDuplicateRows()
    {
        var source = new FlakySource { FailTimestamps = true };
        var ledger = new LedgerService(() => source, "0x70997970C51812dc3A010C7d01b50e0d17dc79C8", () => null);
        await Assert.ThrowsAsync<HttpRequestException>(() => ledger.SnapshotAsync(default));

        source.FailTimestamps = false;
        var snap = await ledger.SnapshotAsync(default);

        Assert.Equal(2, snap.Entries.Count);
    }

    [Fact]
    public async Task StopsConfirmationsBehindTheHead()
    {
        var source = new FlakySource { Latest = 13 };
        var ledger = new LedgerService(() => source, "0x70997970C51812dc3A010C7d01b50e0d17dc79C8", () => null, confirmations: 12);

        var snap = await ledger.SnapshotAsync(default);

        Assert.Single(snap.Entries);
        Assert.Equal(1UL, snap.ScannedToBlock);
    }
}
