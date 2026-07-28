using System.Collections.Concurrent;
using Symposia.Identity.Domain;

namespace Symposia.Identity.Gateway;

/// <summary>
/// Reverse-resolution read model (address ↔ symposia_identity_id) and the
/// lazy-creation trigger point (FR9): a wallet's binding is recorded here the
/// first time an identity-requiring signal occurs (a successful SIWE verify),
/// not on tracking-only activity. The id itself is always independently
/// re-derivable from the address (<see cref="SymposiaIdentityId.Derive"/>);
/// this store exists for query convenience and reverse lookup, per the Arch
/// pass — chain consent/capability state remains the source of truth.
///
/// Production wiring replaces this with the Postgres `identity_binding` table
/// described in the Arch pass; this in-memory store is a scaffold for the
/// same interface.
/// </summary>
public interface IIdentityBindingStore
{
    /// <summary>Returns the existing binding, or creates one on first identity-requiring
    /// signal for this wallet (FR9).</summary>
    Guid GetOrCreate(WalletAddress wallet, string createdVia);

    Guid? TryResolveByWallet(WalletAddress wallet);

    WalletAddress? TryResolveByIdentityId(Guid identityId);
}

public sealed class InMemoryIdentityBindingStore : IIdentityBindingStore
{
    private readonly ConcurrentDictionary<string, Guid> _byWallet = new();
    private readonly ConcurrentDictionary<Guid, WalletAddress> _byId = new();

    public Guid GetOrCreate(WalletAddress wallet, string createdVia)
    {
        return _byWallet.GetOrAdd(wallet.Value, _ =>
        {
            var id = SymposiaIdentityId.Derive(wallet);
            _byId[id] = wallet;
            return id;
        });
    }

    public Guid? TryResolveByWallet(WalletAddress wallet) =>
        _byWallet.TryGetValue(wallet.Value, out var id) ? id : null;

    public WalletAddress? TryResolveByIdentityId(Guid identityId) =>
        _byId.TryGetValue(identityId, out var wallet) ? wallet : null;
}
