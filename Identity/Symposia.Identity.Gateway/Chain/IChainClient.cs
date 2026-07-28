using Symposia.Identity.Domain;

namespace Symposia.Identity.Gateway.Chain;

/// <summary>
/// The Identity Gateway's only path to the authoritative chain state
/// (ConsentRegistry / CapabilityRegistry). The Gateway never decides whether
/// a grant/revocation/capability is valid — it relays a wallet-signed request
/// to the chain and reports back what the chain decided (Arch: "the
/// Postgres read model is an optimization, never the source of truth for a
/// rejection decision").
/// </summary>
public interface IChainClient
{
    Task<ulong> GetNonceAsync(WalletAddress wallet);

    Task<string> GrantConsentAsync(
        WalletAddress wallet,
        byte[] tenantId,
        IReadOnlyList<Permission> permissions,
        byte[] grantSourceHash,
        byte[] grantWordingHash,
        ulong nonce,
        ulong deadline,
        byte[] signature);

    Task<string> RevokeConsentAsync(
        WalletAddress wallet,
        byte[] tenantId,
        IReadOnlyList<Permission> permissions,
        ulong nonce,
        ulong deadline,
        byte[] signature);

    Task<bool> HasActiveConsentAsync(WalletAddress wallet, byte[] tenantId, Permission permission);

    Task<(bool Granted, DateTimeOffset? GrantedAt, byte[] GrantSourceHash, byte[] GrantWordingHash)> GetConsentStateAsync(
        WalletAddress wallet, byte[] tenantId, Permission permission);

    /// <summary>Reverts on-chain (via <see cref="ChainCallException"/>) if no active
    /// consent grant exists — the structural enforcement point for FR5.</summary>
    Task<ulong> IssueCapabilityAsync(WalletAddress wallet, byte[] tenantId, Permission permission);
}

/// <summary>Thrown when the chain rejects a call (e.g., a Solidity <c>require</c> revert).</summary>
public sealed class ChainCallException(string message) : Exception(message);
