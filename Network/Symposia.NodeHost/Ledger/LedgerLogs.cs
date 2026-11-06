using System.Numerics;

namespace Symposia.NodeHost.Ledger;

/// <summary>
/// One decoded on-chain event that touches this node's stake, rewards or penalties (issue #161).
/// <see cref="Service"/> is the on-chain Node Service ordinal (<c>INodeRegistry.NodeType</c>).
/// </summary>
public abstract record LedgerLog(ulong Block, long LogIndex, string TxHash, int Service);

/// <summary><c>StakingNodeRegistry.ServiceRegistered</c>: a service was registered with <paramref name="Stake"/>.</summary>
public sealed record ServiceRegisteredLog(ulong Block, long LogIndex, string TxHash, int Service, BigInteger Stake)
    : LedgerLog(Block, LogIndex, TxHash, Service);

/// <summary><c>StakingNodeRegistry.ServiceStakeChanged</c>; reason 0 Register, 1 TopUp, 2 Withdraw, 3 Slash.</summary>
public sealed record StakeChangedLog(ulong Block, long LogIndex, string TxHash, int Service, BigInteger Previous, BigInteger Current, int Reason)
    : LedgerLog(Block, LogIndex, TxHash, Service);

/// <summary><c>StakingNodeRegistry.ServiceUnstakeRequested</c>.</summary>
public sealed record UnstakeRequestedLog(ulong Block, long LogIndex, string TxHash, int Service, BigInteger Amount, ulong CooldownEnd)
    : LedgerLog(Block, LogIndex, TxHash, Service);

/// <summary><c>StakingNodeRegistry.ServiceStatusChanged</c>; statuses are <c>INodeRegistry.NodeStatus</c> ordinals.</summary>
public sealed record StatusChangedLog(ulong Block, long LogIndex, string TxHash, int Service, int Previous, int Current)
    : LedgerLog(Block, LogIndex, TxHash, Service);

/// <summary><c>RewardDistributor.ServiceSettled</c>: one service's settlement for an epoch.</summary>
public sealed record ServiceSettledLog(ulong Block, long LogIndex, string TxHash, int Service, ulong Epoch, int Reason, BigInteger Amount, BigInteger Paid, BigInteger Held)
    : LedgerLog(Block, LogIndex, TxHash, Service);

/// <summary><c>RewardDistributor.ServiceReserveClaimed</c>: held reserve claimed early by the operator.</summary>
public sealed record ReserveClaimedLog(ulong Block, long LogIndex, string TxHash, int Service, BigInteger Amount)
    : LedgerLog(Block, LogIndex, TxHash, Service);

/// <summary>Reads this node's ledger events from the chain.</summary>
public interface ILedgerLogSource
{
    Task<ulong> LatestBlockAsync(CancellationToken ct);

    /// <summary>First block worth scanning (where the registry was deployed); 0 when unknown.</summary>
    Task<ulong> StartBlockAsync(CancellationToken ct);

    /// <summary>Events for <paramref name="node"/> in [<paramref name="from"/>, <paramref name="to"/>], any order.</summary>
    Task<IReadOnlyList<LedgerLog>> LogsAsync(string node, ulong from, ulong to, CancellationToken ct);

    Task<long> BlockTimestampAsync(ulong block, CancellationToken ct);

    /// <summary>Reward reserve currently held for the node's <paramref name="service"/>.</summary>
    Task<BigInteger> ReserveAsync(string node, int service, CancellationToken ct);
}
