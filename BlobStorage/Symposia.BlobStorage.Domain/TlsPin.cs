using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Symposia.BlobStorage.Domain;

/// <summary>
/// Certificate pinning shared by the Credential Authority and gateways (#245 mTLS). The fingerprint is the same wire
/// format as storage-node pinning (<c>StorageTlsProof.Fingerprint</c>, #142; to be merged in #252): lower-case hex SHA-256 of the DER
/// certificate, so one runbook command produces pins for both. A test keeps the two definitions identical.
/// </summary>
public static class TlsPin
{
    public static string Fingerprint(byte[] certificateDer) => Convert.ToHexStringLower(SHA256.HashData(certificateDer));

    public static string Fingerprint(X509Certificate certificate) => Fingerprint(certificate.GetRawCertData());

    /// <summary>TLS Web Server Authentication (what a gateway requires of the Authority).</summary>
    public const string ServerAuth = "1.3.6.1.5.5.7.3.1";

    /// <summary>TLS Web Client Authentication (what the Authority requires of a gateway).</summary>
    public const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// A configured pin in canonical form: colons and whitespace removed, lower case. Throws unless it is a SHA-256
    /// fingerprint (64 hex digits), so a pin copied with colons from openssl works and a typo fails at startup rather
    /// than silently matching nothing (review: Carl #6).
    /// </summary>
    public static string Normalize(string pin)
    {
        var hex = new string(pin.Where(c => c != ':' && !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit)
            ? hex
            : throw new FormatException($"'{pin}' is not a SHA-256 certificate fingerprint (64 hex digits, colons allowed).");
    }

    /// <summary>
    /// Whether the certificate is one of <paramref name="pins"/> (canonical, see <see cref="Normalize"/>), inside its
    /// validity period and, when it declares extended key usages, allowed <paramref name="usage"/> (review: Lenny #7).
    /// </summary>
    public static bool Matches(X509Certificate? certificate, IEnumerable<string> pins, DateTimeOffset now, string? usage = null)
    {
        if (certificate is null)
        {
            return false;
        }

        var fingerprint = Fingerprint(certificate);
        if (!pins.Any(p => string.Equals(p, fingerprint, StringComparison.Ordinal)))
        {
            return false;
        }

        if (certificate is not X509Certificate2 c2)
        {
            return true;
        }

        if (c2.NotBefore.ToUniversalTime() > now.UtcDateTime || c2.NotAfter.ToUniversalTime() <= now.UtcDateTime)
        {
            return false;
        }

        var eku = c2.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usage is null || eku is null || eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == usage);
    }

    /// <summary>A certificate with no private key (a CA or a pin source): PEM or DER.</summary>
    public static X509Certificate2 LoadCertificate(string path) => X509CertificateLoader.LoadCertificateFromFile(path);

    /// <summary>Loads a PFX, or a PEM certificate with its PEM key (in the same file or <paramref name="keyPath"/>).</summary>
    public static X509Certificate2 Load(string path, string? keyPath = null, string? password = null)
    {
        if (path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }

        using var pem = X509Certificate2.CreateFromPemFile(path, string.IsNullOrEmpty(keyPath) ? path : keyPath);
        // Re-import so the key is usable by SslStream on every platform (ephemeral PEM keys aren't on Windows).
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }
}
