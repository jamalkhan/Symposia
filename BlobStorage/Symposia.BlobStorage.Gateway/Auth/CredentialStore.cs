using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain.Credentials;

namespace Symposia.BlobStorage.Gateway.Auth;

/// <summary>
/// The gateway's in-memory credential state (#3): the Credential Authority's latest snapshot, merged with
/// credentials from configuration. Lookups are dictionary reads, so authorization adds no network call to
/// the request path. Configured credentials predate the Authority: each is a tenant-wide read-write manager
/// of its tenant, and exists only on gateways that list it (development and bootstrap deployments).
/// A credential the cache doesn't know is refused (fail-closed), including before the first snapshot arrives.
/// </summary>
public sealed class CredentialStore
{
    private readonly Dictionary<string, CredentialGrant> _static;
    private State _state = new(0, new Dictionary<string, CredentialGrant>(), new Dictionary<string, TenantStatus>(), []);

    private sealed record State(long Version, IReadOnlyDictionary<string, CredentialGrant> ByAccessKey, IReadOnlyDictionary<string, TenantStatus> Tenants,
        IReadOnlyList<PolicyRule> Rules);

    public CredentialStore(IOptions<GatewayOptions> options)
    {
        _static = options.Value.Credentials.ToDictionary(c => c.AccessKeyId, c => new CredentialGrant(
            $"static:{c.AccessKeyId}", c.AccessKeyId, c.TenantId, c.TenantId, $"config:{c.AccessKeyId}", AccessLevel.ReadWrite, ResourceScope.Tenant,
            null, [], CanManage: true, CredentialStatus.Active, DateTimeOffset.MinValue, Secret: c.SecretAccessKey));
    }

    /// <summary>The Authority state version this gateway enforces (0 = none received yet).</summary>
    public long Version => _state.Version;

    /// <summary>When the last snapshot was applied; drives the staleness metric.</summary>
    public DateTimeOffset LastApplied { get; private set; }

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

    /// <summary>Installs a snapshot unless an equal or newer one is already in place; returns whether it was applied.</summary>
    public bool Apply(AuthoritySnapshot snapshot)
    {
        var next = new State(snapshot.Version,
            snapshot.Credentials.ToDictionary(c => c.AccessKeyId, StringComparer.Ordinal),
            snapshot.Tenants.ToDictionary(t => t.TenantId, t => t.Status, StringComparer.Ordinal),
            snapshot.Rules);
        // Compare-and-swap: the Watch and poll loops pull concurrently, and a slower, older snapshot must never
        // replace a newer one (that would un-revoke a credential until the next pull; review: Frank #9).
        while (true)
        {
            var current = _state;
            if (snapshot.Version <= current.Version && current.Version != 0)
            {
                return false;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, next, current), current))
            {
                LastApplied = DateTimeOffset.UtcNow;
                return true;
            }
        }
    }
}
