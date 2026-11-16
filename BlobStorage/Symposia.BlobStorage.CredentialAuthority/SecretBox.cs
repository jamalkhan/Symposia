using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Symposia.BlobStorage.CredentialAuthority;

/// <summary>
/// Credential secrets at rest (#3 FR3.11), under a rotatable keyring (#245 FR7). SigV4 needs the secret itself to
/// verify a signature, so a one-way hash alone can't authenticate requests: the secret is stored AES-256-GCM
/// encrypted beside a SHA-256 hash for lookups and checks, and is never returned by any API after issuance. Only
/// gateways receive it, over the mTLS + service-token channel, to verify signatures in memory.
/// </summary>
/// <remarks>
/// <para>Ciphertexts name the key that sealed them: <c>k1:&lt;b64&gt;</c> (#3, no associated data) or
/// <c>k2:2:&lt;b64&gt;</c> (#245: the credential ID bound in as AES-GCM associated data, so a sealed secret copied
/// onto another row won't open). New secrets are sealed with the active key; any configured key opens what it sealed.</para>
/// <para>A ciphertext naming a key that isn't configured fails loudly; it is never served as a secret (AC9).</para>
/// </remarks>
public sealed partial class Keyring
{
    private readonly IReadOnlyDictionary<string, byte[]> _keys;

    public Keyring(IReadOnlyDictionary<string, string> keys, string active, SealFormat format = SealFormat.Bound)
    {
        Format = format;
        if (keys.Count == 0)
        {
            throw new InvalidOperationException("CredentialAuthority: no master key configured (MasterKeys, or the legacy MasterKey).");
        }

        foreach (var label in keys.Keys.Where(l => !Label().IsMatch(l)))
        {
            throw new InvalidOperationException($"CredentialAuthority: master key label '{label}' must look like k1, k2, ...");
        }

        _keys = keys.ToDictionary(k => k.Key, k => Convert.FromBase64String(k.Value) is { Length: 32 } bytes ? bytes
            : throw new InvalidOperationException($"CredentialAuthority: master key {k.Key} must be 32 bytes, base64."));
        Active = keys.ContainsKey(active) ? active : throw new InvalidOperationException($"CredentialAuthority: ActiveKey '{active}' is not in MasterKeys.");
        if (format == SealFormat.Legacy && Active != "k1")
        {
            // The previous release reads only k1-sealed, unbound secrets: a k2 seal would be unreadable to it (Frank #2).
            throw new InvalidOperationException("CredentialAuthority: ActiveKey other than k1 needs SealFormat Bound, set once every replica runs this release.");
        }
    }

    /// <summary>How new secrets are sealed.</summary>
    public SealFormat Format { get; }

    /// <summary>The keyring from options: <c>MasterKeys</c> + <c>ActiveKey</c>, or the #3 single <c>MasterKey</c> as <c>k1</c>.</summary>
    public static Keyring From(AuthorityOptions o)
    {
        var keys = new Dictionary<string, string>(o.MasterKeys, StringComparer.Ordinal);
        if (o.MasterKey.Length > 0)
        {
            keys.TryAdd("k1", o.MasterKey);
        }

        return new Keyring(keys, o.ActiveKey.Length > 0 ? o.ActiveKey : keys.ContainsKey("k1") && keys.Count == 1 ? "k1" : o.ActiveKey, o.SealFormat);
    }

    /// <summary>The label new secrets are sealed with.</summary>
    public string Active { get; }

    public IReadOnlyCollection<string> Labels => [.. _keys.Keys];

    /// <summary>Seals under the active key, bound to <paramref name="credentialId"/>.</summary>
    public string Seal(string secret, string credentialId)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(secret);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_keys[Active], 16);
        if (Format == SealFormat.Legacy)
        {
            aes.Encrypt(nonce, plain, cipher, tag); // the #3 format, readable by the previous release
            return $"{Active}:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
        }

        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(credentialId));
        return $"{Active}:2:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The key label a ciphertext names.</summary>
    public static string LabelOf(string sealedSecret) => sealedSecret[..Math.Max(0, sealedSecret.IndexOf(':'))];

    public string Open(string sealedSecret, string credentialId)
    {
        var label = LabelOf(sealedSecret);
        if (!_keys.TryGetValue(label, out var key))
        {
            throw new CryptographicException($"Secret sealed under unknown key version '{label}'; is that key missing from MasterKeys?");
        }

        var rest = sealedSecret[(label.Length + 1)..];
        var bound = rest.StartsWith("2:", StringComparison.Ordinal);
        var raw = new byte[rest.Length];
        if (!Convert.TryFromBase64String(bound ? rest[2..] : rest, raw, out var length) || length < 28)
        {
            // Truncated or corrupt: the same failure as a bad tag, so callers handle one exception type.
            throw new CryptographicException($"Secret sealed under '{label}' is malformed.");
        }

        raw = raw[..length];
        var plain = new byte[raw.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain, bound ? Encoding.UTF8.GetBytes(credentialId) : null);
        return Encoding.UTF8.GetString(plain);
    }

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>A new access key ID (AKIA-style, 20 chars) and 40-char secret.</summary>
    public static (string AccessKeyId, string Secret) NewKeyPair()
    {
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var id = "SYM" + new string(RandomNumberGenerator.GetItems<char>(upper, 17));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(30));
        return (id, secret);
    }

    [GeneratedRegex("^k[0-9]+$")]
    private static partial Regex Label();
}
