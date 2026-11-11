namespace Symposia.Blockchain.Gateway.Chain;

/// <summary>
/// The anvil history bound shared by LocalDev (NodeHost) and the chain test fixtures (issue #210), so they
/// can't drift apart.
/// </summary>
public static class AnvilArgs
{
    /// <summary>
    /// States anvil keeps: ~2 minutes at LocalDev's 2 s block time. Every pinned-block read in the repo reads
    /// the latest block, and the ledger's start-block search falls back without archive state (#182).
    /// </summary>
    public const int HistoryStates = 64;

    /// <summary>
    /// --prune-history: keep only <see cref="HistoryStates"/> states in memory and never spill history to
    /// ~/.foundry/anvil/tmp, which a hard-killed anvil leaks. --state, if passed, still dumps the current state.
    /// </summary>
    public static IReadOnlyList<string> PruneHistory { get; } = ["--prune-history", HistoryStates.ToString()];

    /// <summary>The bare test-fixture launch: <paramref name="port"/>, pruned, silent.</summary>
    public static IReadOnlyList<string> Fixture(int port) => ["--port", port.ToString(), .. PruneHistory, "--silent"];
}
