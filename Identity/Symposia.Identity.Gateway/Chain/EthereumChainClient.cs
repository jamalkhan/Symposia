using Microsoft.Extensions.Options;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
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
    private readonly string _consentRegistryAddress;
    private readonly string _capabilityRegistryAddress;

    public EthereumChainClient(IOptions<GatewayOptions> options)
    {
        var o = options.Value;
        var account = new Account(o.RelayerPrivateKey);
        _web3 = new Web3(account, o.ChainRpcUrl);
        _consentRegistryAddress = o.ConsentRegistryAddress;
        _capabilityRegistryAddress = o.CapabilityRegistryAddress;
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
        byte[] signature)
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

        return await SendAsync(_consentRegistryAddress, function);
    }

    public async Task<string> RevokeConsentAsync(
        WalletAddress wallet,
        byte[] tenantId,
        IReadOnlyList<Permission> permissions,
        ulong nonce,
        ulong deadline,
        byte[] signature)
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

        return await SendAsync(_consentRegistryAddress, function);
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

    public async Task<ulong> IssueCapabilityAsync(WalletAddress wallet, byte[] tenantId, Permission permission)
    {
        var function = new IssueCapabilityFunction { Wallet = wallet.Value, TenantId = tenantId, Permission = (byte)permission };
        var handler = _web3.Eth.GetContractTransactionHandler<IssueCapabilityFunction>();

        TransactionReceipt receipt;
        try
        {
            receipt = await handler.SendRequestAndWaitForReceiptAsync(_capabilityRegistryAddress, function);
        }
        catch (Exception ex)
        {
            throw new ChainCallException($"CapabilityRegistry.issueCapability reverted: {ex.Message}");
        }

        if (receipt.Status?.Value != 1)
        {
            throw new ChainCallException("CapabilityRegistry: no active consent");
        }

        var decoded = receipt.DecodeAllEvents<CapabilityIssuedEventDto>();
        var tokenIdHex = decoded.FirstOrDefault()?.Event.TokenId
            ?? throw new ChainCallException("CapabilityRegistry: issued event missing from receipt");
        return (ulong)tokenIdHex;
    }

    private async Task<string> SendAsync<TFunction>(string contractAddress, TFunction function)
        where TFunction : FunctionMessage, new()
    {
        var handler = _web3.Eth.GetContractTransactionHandler<TFunction>();
        try
        {
            var receipt = await handler.SendRequestAndWaitForReceiptAsync(contractAddress, function);
            if (receipt.Status?.Value != 1)
            {
                throw new ChainCallException($"{typeof(TFunction).Name} reverted");
            }

            return receipt.TransactionHash;
        }
        catch (ChainCallException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ChainCallException($"{typeof(TFunction).Name} reverted: {ex.Message}");
        }
    }
}
