using System.Security.Cryptography;
using System.Text;

namespace Symposia.BlobStorage.CredentialAuthority;

/// <summary>
/// Credential secrets at rest (#3 FR3.11). SigV4 needs the secret itself to verify a signature, so a one-way
/// hash alone can't authenticate requests: the secret is stored AES-256-GCM encrypted under the master key,
/// beside a SHA-256 hash for lookups and audits, and is never returned by any API after issuance. Only
/// gateways receive it, over the service-token channel, to verify signatures in memory.
/// </summary>
public sealed class SecretBox(byte[] key)
{
    public static SecretBox FromBase64(string key) =>
        new(Convert.FromBase64String(key) is { Length: 32 } bytes ? bytes : throw new InvalidOperationException("CredentialAuthority:MasterKey must be 32 bytes, base64."));

    public string Seal(string secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(secret);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Open(string sealedSecret)
    {
        var raw = Convert.FromBase64String(sealedSecret);
        var plain = new byte[raw.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
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
}
