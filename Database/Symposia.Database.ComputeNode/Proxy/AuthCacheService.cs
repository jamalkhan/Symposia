namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>
/// In-process auth cache subscribing (conceptually) to the credential-management API's
/// create/rotate/drop events (FR3, AC3/AC4), so credential rotation/revocation takes effect
/// without a proxy restart. Never forwards the tenant's raw credential to the compute node --
/// the proxy authenticates the client itself, against this cache, per the #93 architectural plan.
/// </summary>
public sealed class AuthCacheService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string DatabaseId, string Username), CredentialEntry> _entries = [];

    public void Upsert(string databaseId, string username, string secretHash)
    {
        lock (_gate)
        {
            _entries[(databaseId, username)] = new CredentialEntry(databaseId, username, secretHash, Revoked: false);
        }
    }

    public void Revoke(string databaseId, string username)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue((databaseId, username), out var existing))
                _entries[(databaseId, username)] = existing with { Revoked = true };
        }
    }

    public void Drop(string databaseId, string username)
    {
        lock (_gate)
        {
            _entries.Remove((databaseId, username));
        }
    }

    /// <summary>
    /// Authenticates strictly per-database (AUTH-06): a credential valid for one tenant database
    /// is never accepted for another, even if the same username/secret pair exists elsewhere.
    /// </summary>
    public bool Authenticate(string databaseId, string username, string secretHash)
    {
        lock (_gate)
        {
            return _entries.TryGetValue((databaseId, username), out var entry)
                && !entry.Revoked
                && entry.SecretHash == secretHash;
        }
    }
}
