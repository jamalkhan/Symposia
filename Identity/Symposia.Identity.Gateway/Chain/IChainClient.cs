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
        byte[] signature,
        CancellationToken ct = default);

    Task<string> RevokeConsentAsync(
        WalletAddress wallet,
        byte[] tenantId,
        IReadOnlyList<Permission> permissions,
        ulong nonce,
        ulong deadline,
        byte[] signature,
        CancellationToken ct = default);

    Task<bool> HasActiveConsentAsync(WalletAddress wallet, byte[] tenantId, Permission permission);

    Task<(bool Granted, DateTimeOffset? GrantedAt, byte[] GrantSourceHash, byte[] GrantWordingHash)> GetConsentStateAsync(
        WalletAddress wallet, byte[] tenantId, Permission permission);

    /// <summary>Reverts on-chain (via <see cref="ChainCallException"/>) if no active
    /// consent grant exists — the structural enforcement point for FR5.</summary>
    Task<ulong> IssueCapabilityAsync(WalletAddress wallet, byte[] tenantId, Permission permission, CancellationToken ct = default);

    /// <summary>Where a relayed write stands (#222), so a caller resolves a pending write instead of resubmitting it;
    /// null when the node knows neither the transaction nor a receipt for it.</summary>
    Task<ChainTransactionStatus?> GetTransactionStatusAsync(string txHash, CancellationToken ct = default);
}

/// <summary>Why a relayed write didn't confirm (#222). Each maps to one HTTP status in <c>ChainResults</c>.</summary>
public enum ChainFailureKind
{
    /// <summary>The chain refused it (a revert): the caller's request is wrong. Nothing was written.</summary>
    Rejected,

    /// <summary>It ran out of gas: a server-side fault. Nothing was written, so a retry is safe.</summary>
    OutOfGas,

    /// <summary>The RPC failed before the transaction reached the node. Nothing was written, so a retry is safe.</summary>
    ChainUnavailable,

    /// <summary>Broadcast, but no receipt yet: it may still mine. Poll the status, never resubmit.</summary>
    Pending,
}

/// <summary>
/// A relayed write that didn't confirm (#222). <see cref="ChainFailureKind.Pending"/> isn't a failure, but it travels
/// the same way so every write endpoint maps every outcome in one place; <see cref="TxHash"/> is set whenever the
/// transaction was signed.
/// </summary>
public sealed class ChainCallException(string message, ChainFailureKind kind = ChainFailureKind.Rejected, string? txHash = null) : Exception(message)
{
    public ChainFailureKind Kind { get; } = kind;

    public string? TxHash { get; } = txHash;
}

/// <summary>A relayed write's state: <c>pending</c>, <c>confirmed</c> or <c>failed</c> (with <see cref="Code"/>).</summary>
public sealed record ChainTransactionStatus(string Status, string? Code = null, ulong? TokenId = null);
