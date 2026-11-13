using Symposia.Blockchain.Contracts;
using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace Symposia.NodeHost.Ledger;

/// <summary>Reads the ledger events from <c>StakingNodeRegistry</c> and <c>RewardDistributor</c> (issue #161).</summary>
public sealed class NethereumLedgerLogSource(string rpcUrl, string registry, string distributor) : ILedgerLogSource
{
    private readonly Web3 _web3 = new(rpcUrl);
    private ulong? _start;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _accounts = new();

    public async Task<ulong> LatestBlockAsync(CancellationToken ct) =>
        (ulong)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;

    /// <summary>Binary-searches the first block where the registry has code, so a long chain isn't scanned from genesis.</summary>
    public async Task<ulong> StartBlockAsync(CancellationToken ct)
    {
        if (_start is { } cached)
        {
            return cached;
        }

        ulong lo = 0, hi = await LatestBlockAsync(ct);
        if (!await HasCodeAsync(hi))
        {
            return _start ??= hi;
        }

        while (lo < hi)
        {
            ct.ThrowIfCancellationRequested();
            var mid = lo + ((hi - lo) / 2);
            if (await HasCodeAsync(mid))
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return (_start = lo).Value;
    }

    private async Task<bool> HasCodeAsync(ulong block)
    {
        try
        {
            var code = await _web3.Eth.GetCode.SendRequestAsync(registry, new BlockParameter(new HexBigInteger(block)));
            return code is { Length: > 2 };
        }
        catch (RpcResponseException)
        {
            // A node without archive state can't answer for old blocks: scan from there. Transport
            // failures (a 429 from the gateway, say) propagate, so they never pin a start block (#182).
            return true;
        }
    }

    public async Task<IReadOnlyList<LedgerLog>> LogsAsync(string node, ulong from, ulong to, CancellationToken ct)
    {
        var logs = new List<LedgerLog>();
        logs.AddRange((await Events<ServiceRegisteredEvent>(registry, node, from, to)).Select(e =>
            new ServiceRegisteredLog(Block(e), Index(e), Tx(e), e.Event.Service, e.Event.Stake)));
        logs.AddRange((await Events<ServiceStakeChangedEvent>(registry, node, from, to)).Select(e =>
            new StakeChangedLog(Block(e), Index(e), Tx(e), e.Event.Service, e.Event.PreviousStake, e.Event.NewStake, e.Event.Reason)));
        logs.AddRange((await Events<ServiceUnstakeRequestedEvent>(registry, node, from, to)).Select(e =>
            new UnstakeRequestedLog(Block(e), Index(e), Tx(e), e.Event.Service, e.Event.Amount, (ulong)e.Event.CooldownEnd)));
        logs.AddRange((await Events<ServiceStatusChangedEvent>(registry, node, from, to)).Select(e =>
            new StatusChangedLog(Block(e), Index(e), Tx(e), e.Event.Service, e.Event.PreviousStatus, e.Event.NewStatus)));
        logs.AddRange((await Events<ServiceSettledEvent>(distributor, node, from, to)).Select(e =>
            new ServiceSettledLog(Block(e), Index(e), Tx(e), e.Event.Service, (ulong)e.Event.Epoch, e.Event.Reason, e.Event.Amount, e.Event.Paid, e.Event.Held)));
        logs.AddRange((await Events<ServiceReserveClaimedEvent>(distributor, node, from, to)).Select(e =>
            new ReserveClaimedLog(Block(e), Index(e), Tx(e), e.Event.Service, e.Event.Amount)));
        return logs;
    }

    private async Task<List<EventLog<T>>> Events<T>(string contract, string node, ulong from, ulong to)
        where T : IEventDTO, new()
    {
        var ev = _web3.Eth.GetEvent<T>(contract);
        var filter = ev.CreateFilterInput(new object[] { node },
            new BlockParameter(new HexBigInteger(from)), new BlockParameter(new HexBigInteger(to)));
        var all = await ev.GetAllChangesAsync(filter);
        // The node is the first indexed topic of every ledger event; recheck it anyway.
        return all.Where(e => e.Log.Topics.Length > 1 && TopicIsNode(e.Log.Topics[1]?.ToString(), node)).ToList();
    }

    private static bool TopicIsNode(string? topic, string node) =>
        topic is not null && topic.EndsWith(node[2..], StringComparison.OrdinalIgnoreCase);

    /// <summary>The block's hash, or "" when the chain doesn't have it.</summary>
    public async Task<string> BlockHashAsync(ulong block, CancellationToken ct) =>
        (await _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new HexBigInteger(block)))?.BlockHash ?? "";

    public async Task<long> BlockTimestampAsync(ulong block, CancellationToken ct)
    {
        var b = await _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new HexBigInteger(block));
        return (long)b.Timestamp.Value;
    }

    public async Task<BigInteger> ReserveAsync(string node, int service, CancellationToken ct)
    {
        var account = await _web3.Eth.GetContractQueryHandler<AccountOfFunction>()
            .QueryAsync<string>(distributor, new AccountOfFunction { Node = node, Service = (byte)service });
        return await _web3.Eth.GetContractQueryHandler<ReserveBalanceOfFunction>()
            .QueryAsync<BigInteger>(distributor, new ReserveBalanceOfFunction { Account = account });
    }

    /// <summary>Read at latest: a sealed epoch's record never changes, and a pruned LocalDev chain (#210) can't answer for old blocks.</summary>
    public async Task<bool> MetricSubmittedAsync(string node, int service, ulong epoch, CancellationToken ct)
    {
        // accountOf is per (node, service), never per epoch, and this source serves one node.
        if (!_accounts.TryGetValue(service, out var account))
        {
            account = await _web3.Eth.GetContractQueryHandler<AccountOfFunction>()
                .QueryAsync<string>(distributor, new AccountOfFunction { Node = node, Service = (byte)service });
            _accounts[service] = account;
        }

        var record = await _web3.Eth.GetContractQueryHandler<MetricOfFunction>()
            .QueryDeserializingToObjectAsync<MetricRecordOutput>(new MetricOfFunction { Epoch = epoch, Account = account }, distributor);
        return record.Submitted;
    }

    private static ulong Block<T>(EventLog<T> e) => (ulong)e.Log.BlockNumber.Value;

    private static long Index<T>(EventLog<T> e) => (long)e.Log.LogIndex.Value;

    private static string Tx<T>(EventLog<T> e) => e.Log.TransactionHash;
}
