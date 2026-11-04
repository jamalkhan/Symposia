using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Symposia.BlobStorage.StorageNode.Grpc;

/// <summary>
/// The storage node's self-signed TLS certificate for its gRPC port (issue #142). Identity comes not
/// from a CA but from the node key's signature over the fingerprint, published in
/// <c>/v1/node/services</c>; gateways pin that fingerprint. Kept owner-only, renewed on startup when
/// missing or within <see cref="RenewBefore"/> of expiry.
/// </summary>
public static class StorageTlsCertificate
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(365);
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public static X509Certificate2 LoadOrCreate(string path, string nodeAddress, ILogger? logger = null)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, password: null, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter.ToUniversalTime() - DateTime.UtcNow > RenewBefore)
                {
                    return existing;
                }

                logger?.LogInformation("Storage TLS certificate expires {NotAfter}; renewing.", existing.NotAfter);
            }
            catch (CryptographicException ex)
            {
                logger?.LogWarning("Storage TLS certificate at {Path} is unreadable ({Error}); creating a new one.", path, ex.Message);
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={nodeAddress}, O=Symposia storage node", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now + Validity);
        var pfx = created.Export(X509ContentType.Pfx);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, pfx);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
        logger?.LogInformation("Created storage TLS certificate {Fingerprint} at {Path}.", Fingerprint(created), path);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Hex SHA-256 of the DER certificate: what gateways pin.</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
}
