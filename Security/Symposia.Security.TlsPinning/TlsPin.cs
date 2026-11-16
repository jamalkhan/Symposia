using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Symposia.Security.TlsPinning;

/// <summary>Why a presented certificate was or wasn't accepted.</summary>
public enum TlsPinResult
{
    Ok,
    NoCertificate,
    FingerprintMismatch,
    NotYetValid,
    Expired,
    MissingUsage,
}

/// <summary>
/// Certificate pinning: the one implementation (#252), used by gateway→storage-node TLS (#142, where the node's key
/// vouches for the pin on chain) and the Credential Authority's internal mTLS (#245). The fingerprint is a wire
/// contract: lower-case hex SHA-256 of the DER certificate, fixed by a golden test.
/// </summary>
public static class TlsPin
{
    /// <summary>TLS Web Server Authentication.</summary>
    public const string ServerAuth = "1.3.6.1.5.5.7.3.1";

    /// <summary>TLS Web Client Authentication.</summary>
    public const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    /// <summary>How far a peer's clock may run ahead of ours before its fresh certificate looks not-yet-valid.</summary>
    public static readonly TimeSpan DefaultClockSkew = TimeSpan.FromMinutes(5);

    public static string Fingerprint(ReadOnlySpan<byte> certificateDer) => Convert.ToHexStringLower(SHA256.HashData(certificateDer));

    public static string Fingerprint(X509Certificate certificate) => Fingerprint(certificate.GetRawCertData());

    /// <summary>
    /// A pin in canonical form: colons and whitespace removed, lower case. Throws unless it is a SHA-256 fingerprint
    /// (64 hex digits), so a pin copied with colons from openssl works and a typo fails loudly.
    /// </summary>
    public static string Normalize(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return TryNormalize(pin, out var canonical)
            ? canonical
            : throw new FormatException($"'{pin}' is not a SHA-256 certificate fingerprint (64 hex digits, colons allowed).");
    }

    public static bool TryNormalize(string? pin, out string canonical)
    {
        canonical = pin is null ? "" : new string(pin.Where(c => c != ':' && !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return canonical.Length == 64 && canonical.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// Checks a presented certificate against canonical <paramref name="pins"/> (see <see cref="Normalize"/>; compared
    /// ordinally). It must be inside its validity period, with NotBefore allowed up to <paramref name="clockSkew"/>
    /// ahead (inclusive) and NotAfter strict, since for storage nodes expiry is the replay boundary. When it declares
    /// extended key usages they must include <paramref name="usage"/>; one declaring none passes (RFC 5280).
    /// </summary>
    public static TlsPinResult Check(X509Certificate? certificate, IEnumerable<string> pins, DateTimeOffset now, string? usage = null, TimeSpan? clockSkew = null)
    {
        if (certificate is null)
        {
            return TlsPinResult.NoCertificate;
        }

        var fingerprint = Fingerprint(certificate);
        if (!pins.Any(p => string.Equals(p, fingerprint, StringComparison.Ordinal)))
        {
            return TlsPinResult.FingerprintMismatch;
        }

        // A plain X509Certificate carries no validity or extensions: parse it, so the checks fail closed, not open.
        using var parsed = certificate is X509Certificate2 ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var c2 = certificate as X509Certificate2 ?? parsed!;
        var skew = clockSkew ?? DefaultClockSkew;
        if (new DateTimeOffset(c2.NotBefore.ToUniversalTime(), TimeSpan.Zero) > now + (skew < TimeSpan.Zero ? TimeSpan.Zero : skew))
        {
            return TlsPinResult.NotYetValid;
        }

        if (new DateTimeOffset(c2.NotAfter.ToUniversalTime(), TimeSpan.Zero) <= now)
        {
            return TlsPinResult.Expired;
        }

        var eku = c2.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usage is null || eku is null || eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == usage) ? TlsPinResult.Ok : TlsPinResult.MissingUsage;
    }

    public static bool Matches(X509Certificate? certificate, IEnumerable<string> pins, DateTimeOffset now, string? usage = null, TimeSpan? clockSkew = null) =>
        Check(certificate, pins, now, usage, clockSkew) == TlsPinResult.Ok;

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

    /// <summary>A certificate with no private key (a CA or a pin source): PEM or DER.</summary>
    public static X509Certificate2 LoadCertificate(string path) => X509CertificateLoader.LoadCertificateFromFile(path);
}
