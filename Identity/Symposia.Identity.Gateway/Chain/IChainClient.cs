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

    /// <summary>The transaction never reached the node, or the node refused it. Nothing was written, so a retry is safe.</summary>
    ChainUnavailable,

    /// <summary>Broadcast (or possibly broadcast), no receipt yet: it may still mine. Poll the status, never resubmit.</summary>
    Pending,

    /// <summary>It mined, but not as the gateway expects (a success receipt without its event): a server-side fault.</summary>
    Fault,
}

public static class ChainFailureKindExtensions
{
    /// <summary>The wire code, shared by the write endpoints and the status endpoint. Exhaustive: a new kind must be mapped.</summary>
    public static string ToWireName(this ChainFailureKind kind) => kind switch
    {
        ChainFailureKind.Rejected => "rejected",
        ChainFailureKind.OutOfGas => "out_of_gas",
        ChainFailureKind.ChainUnavailable => "chain_unavailable",
        ChainFailureKind.Pending => "pending",
        ChainFailureKind.Fault => "chain_fault",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unmapped chain failure kind"),
    };
}

/// <summary>
/// A relayed write that didn't confirm (#222). <see cref="ChainFailureKind.Pending"/> isn't a failure, but it travels
/// the same way so every write endpoint maps every outcome in one place; <see cref="TxHash"/> is set whenever the
/// transaction was signed. The kind is required, so no failure becomes a 422 without someone deciding it should.
/// </summary>
public sealed class ChainCallException(ChainFailureKind kind, string message, string? txHash = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ChainFailureKind Kind { get; } = kind;

    public string? TxHash { get; } = txHash;
}

public enum ChainTxState
{
    Pending,
    Confirmed,
    Failed,
}

/// <summary>A relayed write's state; <see cref="Failure"/> says why when it failed, <see cref="TokenId"/> is a capability's token.</summary>
public sealed record ChainTransactionStatus(ChainTxState State, ChainFailureKind? Failure = null, ulong? TokenId = null);
