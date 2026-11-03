using Microsoft.AspNetCore.Identity;

namespace Symposia.NodeHost.Settings;

/// <summary>
/// Admin password hash and relayer key, in an owner-only secrets.json kept
/// apart from settings (FR-C2, FR-E2). Neither value is ever returned to the UI.
/// </summary>
public sealed class SecretStore(HostPaths paths)
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object User = new();
    private readonly object _gate = new();

    private sealed class Secrets
    {
        public string? PasswordHash { get; set; }

        public string? RelayerPrivateKey { get; set; }
    }

    public bool HasPassword => Load().PasswordHash is not null;

    public bool HasRelayerKey => !string.IsNullOrEmpty(Load().RelayerPrivateKey);

    public string? RelayerPrivateKey => Load().RelayerPrivateKey;

    public void SetPassword(string password) => Update(s => s.PasswordHash = Hasher.HashPassword(User, password));

    /// <summary>Sets the admin password only if none exists yet, atomically; false if one already does.</summary>
    public bool TrySetInitialPassword(string password)
    {
        lock (_gate)
        {
            var secrets = SecureFile.Read<Secrets>(paths.Secrets) ?? new Secrets();
            if (secrets.PasswordHash is not null)
            {
                return false;
            }

            secrets.PasswordHash = Hasher.HashPassword(User, password);
            SecureFile.Write(paths.Secrets, secrets, ownerOnly: true);
            return true;
        }
    }

    public bool VerifyPassword(string password)
    {
        var hash = Load().PasswordHash;
        return hash is not null && Hasher.VerifyHashedPassword(User, hash, password) != PasswordVerificationResult.Failed;
    }

    public void SetRelayerKey(string? key) => Update(s => s.RelayerPrivateKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim());

    private Secrets Load()
    {
        lock (_gate)
        {
            return SecureFile.Read<Secrets>(paths.Secrets) ?? new Secrets();
        }
    }

    private void Update(Action<Secrets> change)
    {
        lock (_gate)
        {
            var secrets = SecureFile.Read<Secrets>(paths.Secrets) ?? new Secrets();
            change(secrets);
            SecureFile.Write(paths.Secrets, secrets, ownerOnly: true);
        }
    }
}
