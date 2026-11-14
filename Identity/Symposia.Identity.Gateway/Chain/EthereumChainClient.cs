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
    private readonly GatewayOptions _options;
    private readonly string _consentRegistryAddress;
    private readonly string _capabilityRegistryAddress;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    // Signing and broadcast share one nonce sequence; the receipt wait happens outside the gate (as RelayerAccount, #163).
    // One process per relayer key: the gate doesn't span replicas.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EthereumChainClient(IOptions<GatewayOptions> options, ILogger<EthereumChainClient> logger)
        : this(options.Value, new NethereumChainTransport(options.Value.RelayerPrivateKey, options.Value.ChainRpcUrl), TimeProvider.System, logger)
    {
    }

    internal EthereumChainClient(GatewayOptions options, NethereumChainTransport transport, TimeProvider time, ILogger logger)
        : this(options, transport, transport.Web3, time, logger)
    {
    }

    /// <param name="reads">Queries (nonces, consent state); writes go through <paramref name="transport"/> only.</param>
    internal EthereumChainClient(GatewayOptions options, IChainTransport transport, Web3 reads, TimeProvider time, ILogger logger)
    {
        _options = options;
        _transport = transport;
        _web3 = reads;
        _consentRegistryAddress = options.ConsentRegistryAddress;
        _capabilityRegistryAddress = options.CapabilityRegistryAddress;
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

        return (await WriteAsync(_consentRegistryAddress, function, typeof(GrantConsentFunction).Name, typeof(GrantConsentFunction).Name, $"{typeof(GrantConsentFunction).Name} reverted", ct)).TransactionHash;
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

        return (await WriteAsync(_consentRegistryAddress, function, typeof(RevokeConsentFunction).Name, typeof(RevokeConsentFunction).Name, $"{typeof(RevokeConsentFunction).Name} reverted", ct)).TransactionHash;
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
        var receipt = await WriteAsync(_capabilityRegistryAddress, function, "issueCapability", "CapabilityRegistry.issueCapability",
            "CapabilityRegistry: no active consent", ct);
        // It mined and spent gas: the caller did nothing wrong, so this is a server fault, never a rejection.
        return TokenIdOf(receipt) ?? throw Logged(new ChainCallException(ChainFailureKind.Fault,
            "CapabilityRegistry: issued event missing from receipt", receipt.TransactionHash), "issueCapability");
    }

    public async Task<ChainTransactionStatus?> GetTransactionStatusAsync(string txHash, CancellationToken ct = default)
    {
        var receipt = await _transport.GetReceiptAsync(txHash, ct);
        if (receipt is null)
        {
            return await _transport.GetTransactionAsync(txHash, ct) is null ? null : new ChainTransactionStatus(ChainTxState.Pending);
        }

        if (receipt.Status?.Value == 1)
        {
            var tokenId = string.Equals(receipt.To, _capabilityRegistryAddress, StringComparison.OrdinalIgnoreCase) ? TokenIdOf(receipt) : null;
            return new ChainTransactionStatus(ChainTxState.Confirmed, TokenId: tokenId);
        }

        var tx = await _transport.GetTransactionAsync(txHash, ct);
        var kind = GasHeadroom.RanOutOfGas(receipt.GasUsed.Value, tx?.Gas?.Value) ? ChainFailureKind.OutOfGas : ChainFailureKind.Rejected;
        return new ChainTransactionStatus(ChainTxState.Failed, kind);
    }

    private static ulong? TokenIdOf(TransactionReceipt receipt) =>
        receipt.DecodeAllEvents<CapabilityIssuedEventDto>().FirstOrDefault() is { } issued ? (ulong)issued.Event.TokenId : null;

    /// <summary>
    /// Signs (the hash is known from here on), broadcasts, and waits up to ReceiptTimeout for a successful receipt
    /// (#222). Only a failure known to have left nothing behind (a revert or RPC fault while signing, a send the node
    /// never received or refused) is reported as such; once bytes may have reached the node, anything short of a
    /// receipt is <see cref="ChainFailureKind.Pending"/>, because the transaction may still mine. The client's
    /// cancellation never cuts a broadcast short; it ends the receipt wait, as Pending.
    /// </summary>
    /// <param name="name">For logs and the receipt-failure wording (#219).</param>
    /// <param name="revertPrefix">The pre-broadcast revert wording, kept as before for clients matching on it.</param>
    private async Task<TransactionReceipt> WriteAsync<TFunction>(string contract, TFunction function, string name, string revertPrefix,
        string revertMessage, CancellationToken ct)
        where TFunction : FunctionMessage, new()
    {
        if (!await _gate.WaitAsync(_options.GateTimeout, ct))
        {
            throw Logged(new ChainCallException(ChainFailureKind.ChainUnavailable, $"{name}: chain unavailable: relayer busy"), name);
        }

        string hash;
        try
        {
            string signed;
            try
            {
                using var step = new CancellationTokenSource(_options.RpcStepTimeout, _time);
                signed = await _transport.SignAsync(contract, function, step.Token);
            }
            catch (Exception ex)
            {
                await ResetNonceQuietlyAsync();
                var kind = ex is OperationCanceledException ? ChainFailureKind.ChainUnavailable : ClassifyPreBroadcast(ex);
                throw Logged(new ChainCallException(kind,
                    kind == ChainFailureKind.Rejected ? $"{revertPrefix} reverted: {ex.Message}" : $"{name}: chain unavailable: {ex.Message}", null, ex), name);
            }

            hash = HashOf(signed);
            try
            {
                using var step = new CancellationTokenSource(_options.RpcStepTimeout, _time);
                await _transport.SendRawAsync(signed, step.Token);
            }
            catch (Exception ex) when (ClassifySend(ex) == SendOutcome.NotSent)
            {
                await ResetNonceQuietlyAsync();
                throw Logged(new ChainCallException(ChainFailureKind.ChainUnavailable, $"{name}: chain unavailable: {ex.Message}", hash, ex), name);
            }
            catch (Exception ex)
            {
                // The node may have it: wait for it like any broadcast. Never reset the nonce or report it as safe to retry.
                _logger.LogWarning("Identity chain: {Function} send of {TxHash} ended ambiguously, waiting for it: {Reason}", name, hash, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        var receipt = await WaitForReceiptAsync(hash, ct);
        if (receipt is null)
        {
            if (!ct.IsCancellationRequested && !await KnownAsync(hash))
            {
                // Dropped (or never received): its nonce would hold every later write behind a gap. Re-read it from the
                // chain. If the transaction lands after all, a write reusing its nonce fails cleanly; nothing doubles.
                await ResetNonceUnderGateAsync();
                _logger.LogWarning("Identity chain: {Function} {TxHash} is unknown to the node after {Seconds:0}s; nonce re-read from the chain.",
                    name, hash, _options.ReceiptTimeout.TotalSeconds);
            }

            throw Logged(new ChainCallException(ChainFailureKind.Pending,
                $"{name} was broadcast; no receipt after {_options.ReceiptTimeout.TotalSeconds:0}s", hash), name);
        }

        if (receipt.Status?.Value != 1)
        {
            var tx = await TryGetTransactionAsync(hash);
            var outOfGas = GasHeadroom.RanOutOfGas(receipt.GasUsed.Value, tx?.Gas?.Value);
            throw Logged(new ChainCallException(outOfGas ? ChainFailureKind.OutOfGas : ChainFailureKind.Rejected,
                FailureMessage(name, revertMessage, receipt.GasUsed.Value, tx?.Gas?.Value), hash), name);
        }

        return receipt;
    }

    /// <summary>
    /// Polls for the receipt until ReceiptTimeout, every call bounded by it; a lookup error is retried, since the
    /// transaction may still mine. Null on timeout or when the caller goes away.
    /// </summary>
    private async Task<TransactionReceipt?> WaitForReceiptAsync(string hash, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.ReceiptTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            while (true)
            {
                try
                {
                    if (await _transport.GetReceiptAsync(hash, linked.Token) is { } receipt)
                    {
                        return receipt;
                    }
                }
                catch (Exception) when (!linked.IsCancellationRequested)
                {
                }

                await Task.Delay(_options.ReceiptPollInterval, _time, linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<bool> KnownAsync(string hash) => await TryGetTransactionAsync(hash) is not null;

    private async Task<Transaction?> TryGetTransactionAsync(string hash)
    {
        try
        {
            using var step = new CancellationTokenSource(_options.RpcStepTimeout, _time);
            return await _transport.GetTransactionAsync(hash, step.Token);
        }
        catch (Exception)
        {
            // Only the classification depends on this lookup; it must not turn the outcome into a different error.
            return null;
        }
    }

    private async Task ResetNonceUnderGateAsync()
    {
        if (await _gate.WaitAsync(_options.GateTimeout))
        {
            try
            {
                await ResetNonceQuietlyAsync();
            }
            finally
            {
                _gate.Release();
            }
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
            _logger.LogWarning("Identity chain: {Function} {Kind} (tx {TxHash}): {Reason}", name, ex.Kind.ToWireName(), ex.TxHash ?? "none", ex.Message);
        }

        return ex;
    }

    /// <summary>A transaction's hash is the keccak of its signed bytes, for legacy and typed transactions alike.</summary>
    internal static string HashOf(string signedTx) => "0x" + Sha3Keccack.Current.CalculateHashFromHex(signedTx.EnsureHexPrefix());

    /// <summary>
    /// A failure before broadcast: a revert (the estimate ran the call and it reverted) is the caller's; anything
    /// else, including an exception this doesn't recognize, is the chain's, never reported as a rejection.
    /// </summary>
    internal static ChainFailureKind ClassifyPreBroadcast(Exception ex) => ex switch
    {
        SmartContractRevertException or SmartContractCustomErrorRevertException => ChainFailureKind.Rejected,
        RpcResponseException { RpcError: { } e } when e.Code == 3
            || (e.Data?.Type == Newtonsoft.Json.Linq.JTokenType.String && ((string?)e.Data)!.StartsWith("0x", StringComparison.Ordinal))
            || (e.Code == -32000 && (e.Message ?? "").Contains("revert", StringComparison.OrdinalIgnoreCase)) => ChainFailureKind.Rejected,
        _ => ChainFailureKind.ChainUnavailable,
    };

    /// <summary>An RPC or transport fault (the node, not the gateway's code): what the status endpoint answers 503 for.</summary>
    internal static bool IsRpcFault(Exception ex) =>
        ex is RpcResponseException or RpcClientUnknownException or RpcClientTimeoutException or HttpRequestException or TimeoutException;

    internal enum SendOutcome
    {
        /// <summary>The node never got it (no connection) or answered with a refusal: nothing will mine.</summary>
        NotSent,

        /// <summary>It may have reached the node (a timeout, a reset, "already known").</summary>
        Unknown,
    }

    /// <summary>
    /// A failed broadcast is "not sent" only when that's certain: the connection was never made, or the node answered
    /// with an error other than already having it. Everything else (timeouts, resets, cancellation) may have landed.
    /// </summary>
    internal static SendOutcome ClassifySend(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case RpcResponseException r:
                    var message = r.RpcError?.Message ?? r.Message;
                    return message.Contains("already known", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("known transaction", StringComparison.OrdinalIgnoreCase) ? SendOutcome.Unknown : SendOutcome.NotSent;
                case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError }:
                    return SendOutcome.NotSent;
            }
        }

        return SendOutcome.Unknown;
    }

    /// <summary>
    /// The error for a failed receipt: likely out of gas when it used its whole limit (#219), so a gas problem isn't
    /// reported as the caller's consent state; otherwise <paramref name="revertMessage"/>. An INVALID opcode also burns
    /// the whole limit, hence "likely"; an out-of-gas inside a nested call leaves gas over and reads as a revert.
    /// </summary>
    internal static string FailureMessage(string name, string revertMessage, BigInteger gasUsed, BigInteger? gasLimit) =>
        GasHeadroom.RanOutOfGas(gasUsed, gasLimit) ? $"{name} used its whole gas limit ({gasUsed} of {gasLimit}), likely out of gas" : revertMessage;
}
