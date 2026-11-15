using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain.Credentials;

namespace Symposia.BlobStorage.Gateway.Auth;

/// <summary>
/// The gateway's in-memory credential state (#3): the Credential Authority's state, merged with credentials from
/// configuration. Lookups are dictionary reads, so authorization adds no network call to the request path.
/// Configured credentials predate the Authority: each is a tenant-wide read-write manager of its tenant, and exists
/// only on gateways that list it (development and bootstrap deployments). A credential the cache doesn't know is
/// refused (fail-closed), including before the first snapshot arrives.
/// </summary>
/// <remarks>
/// State is immutable and swapped by compare-and-swap, from a full snapshot (<see cref="Apply"/>) or, since #245,
/// a delta (<see cref="ApplyDelta"/>). Immutable maps make a delta cost O(changes · log n), not a rebuild.
/// </remarks>
public sealed class CredentialStore
{
    private readonly Dictionary<string, CredentialGrant> _static;
    private State _state = State.Empty;

    private sealed record State(
        long Version,
        string? Epoch,
        ImmutableDictionary<string, CredentialGrant> ByAccessKey,
        ImmutableDictionary<string, string> AccessKeyById,
        ImmutableDictionary<string, TenantStatus> Tenants,
        ImmutableDictionary<(string Tenant, string Bucket, string Key), IReadOnlyList<PolicyRule>> RuleSets,
        bool Loaded = true)
    {
        public static readonly State Empty = new(0, null, ImmutableDictionary<string, CredentialGrant>.Empty.WithComparers(StringComparer.Ordinal),
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
            ImmutableDictionary<string, TenantStatus>.Empty.WithComparers(StringComparer.Ordinal),
            ImmutableDictionary<(string, string, string), IReadOnlyList<PolicyRule>>.Empty, Loaded: false);

        public IReadOnlyList<PolicyRule> Rules { get; } = RuleSets.Values.SelectMany(r => r).ToList();
    }

    public CredentialStore(IOptions<GatewayOptions> options)
    {
        _static = options.Value.Credentials.ToDictionary(c => c.AccessKeyId, c => new CredentialGrant(
            $"static:{c.AccessKeyId}", c.AccessKeyId, c.TenantId, c.TenantId, $"config:{c.AccessKeyId}", AccessLevel.ReadWrite, ResourceScope.Tenant,
            null, [], CanManage: true, CredentialStatus.Active, DateTimeOffset.MinValue, Secret: c.SecretAccessKey));
    }

    /// <summary>The Authority state version this gateway enforces (0 = none received yet).</summary>
    public long Version => _state.Version;

    /// <summary>The Authority database epoch the cached state came from (null: none yet, or a pre-#245 Authority).</summary>
    public string? Epoch => _state.Epoch;

    /// <summary>When state was last applied (snapshot or delta); drives the staleness metric.</summary>
    public DateTimeOffset LastApplied { get; private set; }

    /// <summary>
    /// A snapshot has been applied. Not the same as <c>Version &gt; 0</c>: an Authority with no mutations yet is
    /// legitimately at version 0, and deltas continue from there.
    /// </summary>
    public bool Loaded => _state.Loaded;

    public CredentialRecord? Get(string accessKeyId) =>
        Grant(accessKeyId) is { Secret: { } secret } g ? new CredentialRecord { AccessKeyId = g.AccessKeyId, SecretAccessKey = secret, TenantId = g.TenantId } : null;

    public CredentialGrant? Grant(string accessKeyId) =>
        _state.ByAccessKey.TryGetValue(accessKeyId, out var fromAuthority) ? fromAuthority
        : _static.GetValueOrDefault(accessKeyId);

    /// <summary>A tenant's status; configured tenants the Authority doesn't know are active.</summary>
    public TenantStatus TenantStatus(string tenantId) =>
        _state.Tenants.TryGetValue(tenantId, out var status) ? status : _static.Values.Any(c => c.TenantId == tenantId) ? Domain.Credentials.TenantStatus.Active
        : Domain.Credentials.TenantStatus.Suspended;

    public IReadOnlyList<PolicyRule> Rules => _state.Rules;

    /// <summary>
    /// Installs a full snapshot unless an equal or newer one from the same database is already in place. A snapshot
    /// from a different epoch (the Authority database was restored or replaced) is installed whatever its version,
    /// so a restore can't freeze the cache behind an unreachable version. Returns whether it was applied.
    /// </summary>
    public bool Apply(AuthoritySnapshot snapshot)
    {
        var next = new State(snapshot.Version, snapshot.Epoch,
            snapshot.Credentials.ToImmutableDictionary(c => c.AccessKeyId, StringComparer.Ordinal),
            snapshot.Credentials.ToImmutableDictionary(c => c.CredentialId, c => c.AccessKeyId, StringComparer.Ordinal),
            snapshot.Tenants.ToImmutableDictionary(t => t.TenantId, t => t.Status, StringComparer.Ordinal),
            snapshot.Rules.GroupBy(r => (r.TenantId, r.Bucket, r.Key ?? ""))
                .ToImmutableDictionary(g => g.Key, g => (IReadOnlyList<PolicyRule>)g.ToList()));
        // Compare-and-swap: sync paths race, and a slower, older snapshot must never replace a newer one (that would
        // un-revoke a credential until the next pull; #3 review: Frank #9).
        return Swap(current => current.Loaded && snapshot.Version <= current.Version && SameEpoch(current.Epoch, snapshot.Epoch) ? null : next);
    }

    /// <summary>What applying a delta did.</summary>
    public enum DeltaResult
    {
        Applied,

        /// <summary>Already at or past the delta's version: nothing to do.</summary>
        Stale,

        /// <summary>The delta doesn't continue from the cached version or epoch, or lacks a secret: take the snapshot.</summary>
        NeedsSnapshot,
    }

    /// <summary>
    /// Applies a delta onto the cached state (#245): only the entities it names change. Refuses a delta that leaves a
    /// gap, comes from another epoch, or names a new credential without its secret; the caller then takes the snapshot.
    /// </summary>
    public DeltaResult ApplyDelta(AuthorityDelta delta)
    {
        var result = DeltaResult.Applied;
        Swap(current =>
        {
            if (!SameEpoch(current.Epoch, delta.Epoch) || !current.Loaded || delta.FromVersion > current.Version)
            {
                result = DeltaResult.NeedsSnapshot;
                return null;
            }

            if (delta.Version <= current.Version)
            {
                result = DeltaResult.Stale;
                return null;
            }

            var byKey = current.ByAccessKey.ToBuilder();
            var byId = current.AccessKeyById.ToBuilder();
            foreach (var id in delta.DeletedCredentialIds)
            {
                if (byId.TryGetValue(id, out var key))
                {
                    byKey.Remove(key);
                    byId.Remove(id);
                }
            }

            foreach (var grant in delta.Credentials)
            {
                var secret = grant.Secret;
                if (secret is null)
                {
                    // Unchanged secret: keep ours. A credential we never saw arriving without one means the delta
                    // and the cache disagree; never guess (#245 Arch §1.3).
                    secret = byId.TryGetValue(grant.CredentialId, out var key) && byKey.TryGetValue(key, out var cached) ? cached.Secret : null;
                    if (secret is null)
                    {
                        result = DeltaResult.NeedsSnapshot;
                        return null;
                    }
                }

                if (byId.TryGetValue(grant.CredentialId, out var oldKey) && oldKey != grant.AccessKeyId)
                {
                    byKey.Remove(oldKey);
                }

                byKey[grant.AccessKeyId] = grant with { Secret = secret };
                byId[grant.CredentialId] = grant.AccessKeyId;
            }

            var tenants = current.Tenants.SetItems(delta.Tenants.Select(t => KeyValuePair.Create(t.TenantId, t.Status)));
            var rules = current.RuleSets.ToBuilder();
            foreach (var set in delta.RuleSets)
            {
                var key = (set.TenantId, set.Bucket, set.Key ?? "");
                if (set.Rules.Count == 0) rules.Remove(key);
                else rules[key] = set.Rules;
            }

            return new State(delta.Version, delta.Epoch ?? current.Epoch, byKey.ToImmutable(), byId.ToImmutable(), tenants, rules.ToImmutable());
        });

        return result;
    }

    private static bool SameEpoch(string? cached, string? incoming) => cached is null || incoming is null || cached == incoming;

    /// <summary>CAS loop: <paramref name="next"/> returns the state to install, or null to leave the current one.</summary>
    private bool Swap(Func<State, State?> next)
    {
        while (true)
        {
            var current = _state;
            if (next(current) is not { } replacement)
            {
                return false;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, replacement, current), current))
            {
                LastApplied = DateTimeOffset.UtcNow;
                return true;
            }
        }
    }
}
