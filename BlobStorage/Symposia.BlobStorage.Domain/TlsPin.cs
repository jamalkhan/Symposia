using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Symposia.BlobStorage.Domain;

/// <summary>
/// Certificate pinning shared by the Credential Authority and gateways (#245 mTLS). The fingerprint is the same wire
/// format as storage-node pinning (<c>StorageTlsProof.Fingerprint</c>, #142): lower-case hex SHA-256 of the DER
/// certificate, so one runbook command produces pins for both. A test keeps the two definitions identical.
/// </summary>
public static class TlsPin
{
    public static string Fingerprint(byte[] certificateDer) => Convert.ToHexStringLower(SHA256.HashData(certificateDer));

    public static string Fingerprint(X509Certificate certificate) => Fingerprint(certificate.GetRawCertData());

    /// <summary>Whether the certificate is one of <paramref name="pins"/> and, when it carries one, not past its expiry.</summary>
    public static bool Matches(X509Certificate? certificate, IEnumerable<string> pins, DateTimeOffset now)
    {
        if (certificate is null)
        {
            return false;
        }

        var fingerprint = Fingerprint(certificate);
        var notAfter = certificate is X509Certificate2 c2 ? new DateTimeOffset(c2.NotAfter.ToUniversalTime(), TimeSpan.Zero) : (DateTimeOffset?)null;
        return pins.Any(p => string.Equals(p, fingerprint, StringComparison.OrdinalIgnoreCase)) && (notAfter is null || notAfter > now);
    }

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
