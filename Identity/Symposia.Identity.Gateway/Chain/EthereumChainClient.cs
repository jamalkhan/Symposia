using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding;
using Nethereum.Contracts;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.JsonRpc.Client;
using Nethereum.Util;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Symposia.Blockchain.Contracts;
using Symposia.Identity.Domain;

namespace Symposia.Identity.Gateway.Chain;

/// <summary>
/// Relays consent/revocation/capability requests to the deployed
/// ConsentRegistry / CapabilityRegistry contracts (issue #21) via the
/// protocol L3's JSON-RPC endpoint. Pays gas from the configured relayer
/// account; the individual's authority comes solely from the wallet
/// signature embedded in each call, not from `msg.sender`.
/// </summary>
public sealed class EthereumChainClient : IChainClient
{
    private readonly Web3 _web3;
    private readonly IChainTransport _transport;
    private readonly string _consentRegistryAddress;
    private readonly string _capabilityRegistryAddress;
    private readonly TimeSpan _receiptTimeout;
    private readonly TimeSpan _receiptPollInterval;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    // Signing and broadcast share one nonce sequence; the receipt wait happens outside the gate (as RelayerAccount, #163).
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EthereumChainClient(IOptions<GatewayOptions> options, ILogger<EthereumChainClient> logger)
        : this(options.Value, new NethereumChainTransport(options.Value.RelayerPrivateKey, options.Value.ChainRpcUrl), TimeProvider.System, logger)
    {
    }

    internal EthereumChainClient(GatewayOptions o, IChainTransport transport, TimeProvider time, ILogger logger)
    {
        _transport = transport;
        _web3 = transport is NethereumChainTransport n ? n.Web3 : new Web3(o.ChainRpcUrl);
        _consentRegistryAddress = o.ConsentRegistryAddress;
        _capabilityRegistryAddress = o.CapabilityRegistryAddress;
        _receiptTimeout = o.ReceiptTimeout;
        _receiptPollInterval = o.ReceiptPollInterval;
        _time = time;
        _logger = logger;
    }

    public async Task<ulong> GetNonceAsync(WalletAddress wallet)
    {
        var handler = _web3.Eth.GetContractQueryHandler<NoncesFunction>();
        var result = await handler.QueryAsync<System.Numerics.BigInteger>(
            _consentRegistryAddress, new NoncesFunction { Wallet = wallet.Value });
        return (ulong)result;
    }

    public async Task<string> GrantConsentAsync(
        WalletAddress wallet,
        byte[] tenantId,
        IReadOnlyList<Permission> permissions,
        byte[] grantSourceHash,
        byte[] grantWordingHash,
        ulong nonce,
        ulong deadline,
        byte[] signature,
        CancellationToken ct = default)
    {
        var function = new GrantConsentFunction
        {
            Wallet = wallet.Value,
            TenantId = tenantId,
            Permissions = permissions.Select(p => (byte)p).ToList(),
            GrantSourceHash = grantSourceHash,
            GrantWordingHash = grantWordingHash,
            Nonce = nonce,
            Deadline = deadline,
            Signature = signature,
        };

        return (await WriteAsync(_consentRegistryAddress, function, typeof(GrantConsentFunction).Name, $"{typeof(GrantConsentFunction).Name} reverted", ct)).TransactionHash;
    }

    public async Task<string> RevokeConsentAsync(
        WalletAddress wallet,
        byte[] tenantId,
        IReadOnlyList<Permission> permissions,
        ulong nonce,
        ulong deadline,
        byte[] signature,
        CancellationToken ct = default)
    {
        var function = new RevokeConsentFunction
        {
            Wallet = wallet.Value,
            TenantId = tenantId,
            Permissions = permissions.Select(p => (byte)p).ToList(),
            Nonce = nonce,
            Deadline = deadline,
            Signature = signature,
        };

        return (await WriteAsync(_consentRegistryAddress, function, typeof(RevokeConsentFunction).Name, $"{typeof(RevokeConsentFunction).Name} reverted", ct)).TransactionHash;
    }

    public async Task<bool> HasActiveConsentAsync(WalletAddress wallet, byte[] tenantId, Permission permission)
    {
        var handler = _web3.Eth.GetContractQueryHandler<HasActiveConsentFunction>();
        return await handler.QueryAsync<bool>(
            _consentRegistryAddress,
            new HasActiveConsentFunction { Wallet = wallet.Value, TenantId = tenantId, Permission = (byte)permission });
    }

    public async Task<(bool Granted, DateTimeOffset? GrantedAt, byte[] GrantSourceHash, byte[] GrantWordingHash)> GetConsentStateAsync(
        WalletAddress wallet, byte[] tenantId, Permission permission)
    {
        var handler = _web3.Eth.GetContractQueryHandler<ConsentStateFunction>();
        var result = await handler.QueryDeserializingToObjectAsync<ConsentStateOutput>(
            new ConsentStateFunction { Wallet = wallet.Value, TenantId = tenantId, Permission = (byte)permission },
            _consentRegistryAddress);

        DateTimeOffset? grantedAt = result.GrantedAt == 0
            ? null
            : DateTimeOffset.FromUnixTimeSeconds((long)result.GrantedAt);

        return (result.Granted, grantedAt, result.GrantSourceHash, result.GrantWordingHash);
    }

    public async Task<ulong> IssueCapabilityAsync(WalletAddress wallet, byte[] tenantId, Permission permission, CancellationToken ct = default)
    {
        var function = new IssueCapabilityFunction { Wallet = wallet.Value, TenantId = tenantId, Permission = (byte)permission };
        var receipt = await WriteAsync(_capabilityRegistryAddress, function, "issueCapability", "CapabilityRegistry: no active consent", ct);
        return TokenIdOf(receipt)
            ?? throw new ChainCallException("CapabilityRegistry: issued event missing from receipt", ChainFailureKind.Rejected, receipt.TransactionHash);
    }

    public async Task<ChainTransactionStatus?> GetTransactionStatusAsync(string txHash, CancellationToken ct = default)
    {
        var receipt = await _transport.GetReceiptAsync(txHash, ct);
        if (receipt is null)
        {
            return await _transport.GetTransactionAsync(txHash, ct) is null ? null : new ChainTransactionStatus("pending");
        }

        if (receipt.Status?.Value == 1)
        {
            var tokenId = string.Equals(receipt.To, _capabilityRegistryAddress, StringComparison.OrdinalIgnoreCase) ? TokenIdOf(receipt) : null;
            return new ChainTransactionStatus("confirmed", TokenId: tokenId);
        }

        var tx = await _transport.GetTransactionAsync(txHash, ct);
        var kind = GasHeadroom.RanOutOfGas(receipt.GasUsed.Value, tx?.Gas?.Value) ? ChainFailureKind.OutOfGas : ChainFailureKind.Rejected;
        return new ChainTransactionStatus("failed", Code: Code(kind));
    }

    /// <summary>The wire code for a failure kind, shared by the write endpoints and the status endpoint.</summary>
    public static string Code(ChainFailureKind kind) => kind switch
    {
        ChainFailureKind.OutOfGas => "out_of_gas",
        ChainFailureKind.ChainUnavailable => "chain_unavailable",
        ChainFailureKind.Pending => "pending",
        _ => "rejected",
    };

    private static ulong? TokenIdOf(TransactionReceipt receipt) =>
        receipt.DecodeAllEvents<CapabilityIssuedEventDto>().FirstOrDefault() is { } issued ? (ulong)issued.Event.TokenId : null;

    /// <summary>
    /// Signs (the hash is known from here on), broadcasts, and waits up to ReceiptTimeout for a successful receipt
    /// (#222). Before the broadcast a failure is <see cref="ChainFailureKind.Rejected"/> or
    /// <see cref="ChainFailureKind.ChainUnavailable"/>; after it, anything short of a receipt is
    /// <see cref="ChainFailureKind.Pending"/>, because the transaction may still mine.
    /// </summary>
    private async Task<TransactionReceipt> WriteAsync<TFunction>(string contract, TFunction function, string name, string revertMessage, CancellationToken ct)
        where TFunction : FunctionMessage, new()
    {
        string hash;
        await _gate.WaitAsync(ct);
        try
        {
            string signed;
            try
            {
                signed = await _transport.SignAsync(contract, function);
            }
            catch (Exception ex)
            {
                await ResetNonceQuietlyAsync();
                var kind = ClassifyPreBroadcast(ex);
                throw Logged(new ChainCallException(kind == ChainFailureKind.Rejected ? $"{name} reverted: {ex.Message}" : $"{name}: chain unavailable: {ex.Message}", kind), name);
            }

            hash = HashOf(signed);
            try
            {
                await _transport.SendRawAsync(signed, ct);
            }
            catch (Exception ex)
            {
                // The node may have it even though the call failed (a timeout after it was received). Ask once.
                if (await KnownAsync(hash))
                {
                    throw Logged(new ChainCallException($"{name} was broadcast; its outcome isn't known yet", ChainFailureKind.Pending, hash), name);
                }

                await ResetNonceQuietlyAsync();
                throw Logged(new ChainCallException($"{name}: chain unavailable: {ex.Message}", ChainFailureKind.ChainUnavailable, hash), name);
            }
        }
        finally
        {
            _gate.Release();
        }

        var receipt = await WaitForReceiptAsync(hash, ct)
            ?? throw Logged(new ChainCallException($"{name} was broadcast; no receipt after {_receiptTimeout.TotalSeconds:0}s", ChainFailureKind.Pending, hash), name);
        if (receipt.Status?.Value != 1)
        {
            var tx = await TryGetTransactionAsync(hash);
            var outOfGas = GasHeadroom.RanOutOfGas(receipt.GasUsed.Value, tx?.Gas?.Value);
            throw Logged(new ChainCallException(FailureMessage(name, revertMessage, receipt.GasUsed.Value, tx?.Gas?.Value),
                outOfGas ? ChainFailureKind.OutOfGas : ChainFailureKind.Rejected, hash), name);
        }

        return receipt;
    }

    /// <summary>Polls for the receipt until ReceiptTimeout; a lookup error is retried, since the transaction may still mine. Null on timeout.</summary>
    private async Task<TransactionReceipt?> WaitForReceiptAsync(string hash, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + _receiptTimeout;
        while (true)
        {
            try
            {
                if (await _transport.GetReceiptAsync(hash, ct) is { } receipt)
                {
                    return receipt;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return null;
            }

            await Task.Delay(_receiptPollInterval, _time, ct);
        }
    }

    private async Task<bool> KnownAsync(string hash) => await TryGetTransactionAsync(hash) is not null;

    private async Task<Transaction?> TryGetTransactionAsync(string hash)
    {
        try
        {
            return await _transport.GetTransactionAsync(hash, CancellationToken.None);
        }
        catch (Exception)
        {
            // Only the classification depends on this lookup; it must not turn the failure into a different error.
            return null;
        }
    }

    private async Task ResetNonceQuietlyAsync()
    {
        try
        {
            await _transport.ResetNonceAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Identity chain: nonce reset failed: {Reason}", ex.Message);
        }
    }

    private ChainCallException Logged(ChainCallException ex, string name)
    {
        if (ex.Kind != ChainFailureKind.Rejected)
        {
            _logger.LogWarning("Identity chain: {Function} {Kind} (tx {TxHash}): {Reason}", name, Code(ex.Kind), ex.TxHash ?? "none", ex.Message);
        }

        return ex;
    }

    /// <summary>A transaction's hash is the keccak of its signed bytes, for legacy and typed transactions alike.</summary>
    internal static string HashOf(string signedTx) => "0x" + Sha3Keccack.Current.CalculateHashFromHex(signedTx.EnsureHexPrefix());

    /// <summary>
    /// A failure before broadcast: a revert (the estimate ran the call and it reverted) is the caller's;
    /// anything else, including an exception this doesn't recognize, is the chain's, never reported as a rejection.
    /// </summary>
    internal static ChainFailureKind ClassifyPreBroadcast(Exception ex) => ex switch
    {
        SmartContractRevertException or SmartContractCustomErrorRevertException => ChainFailureKind.Rejected,
        RpcResponseException r when r.RpcError?.Code == 3 || r.RpcError?.Data is not null
            || (r.RpcError?.Message ?? r.Message).Contains("revert", StringComparison.OrdinalIgnoreCase) => ChainFailureKind.Rejected,
        _ => ChainFailureKind.ChainUnavailable,
    };

    /// <summary>
    /// The error for a failed receipt: likely out of gas when it used its whole limit (#219), so a gas problem isn't
    /// reported as the caller's consent state; otherwise <paramref name="revertMessage"/>. An INVALID opcode also burns
    /// the whole limit, hence "likely"; an out-of-gas inside a nested call leaves gas over and reads as a revert.
    /// </summary>
    internal static string FailureMessage(string name, string revertMessage, BigInteger gasUsed, BigInteger? gasLimit) =>
        GasHeadroom.RanOutOfGas(gasUsed, gasLimit) ? $"{name} used its whole gas limit ({gasUsed} of {gasLimit}), likely out of gas" : revertMessage;
}
