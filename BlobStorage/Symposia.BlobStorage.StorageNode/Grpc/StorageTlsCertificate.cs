using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.StorageNode.Grpc;

/// <summary>
/// The storage node's self-signed TLS certificate for its gRPC port (issue #142). Identity comes not
/// from a CA but from the node key's signature over the fingerprint and expiry, published in
/// <c>/v1/node/services</c>; gateways pin it. The key file is owner-only from creation, written to a
/// random temp name, flushed, then renamed. Renewed when missing, unreadable or within
/// <see cref="RenewBefore"/> of expiry.
/// </summary>
public static class StorageTlsCertificate
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(365);
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public static X509Certificate2 LoadOrCreate(string path, string nodeAddress, ILogger? logger = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
                if (new DateTimeOffset(existing.NotAfter.ToUniversalTime(), TimeSpan.Zero) - at > RenewBefore)
                {
                    return existing;
                }

                logger?.LogInformation("Storage TLS certificate expires {NotAfter}; renewing.", existing.NotAfter);
                existing.Dispose();
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning("Storage TLS certificate at {Path} is unreadable ({Error}); creating a new one.", path, ex.Message);
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={nodeAddress}, O=Symposia storage node", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var created = request.CreateSelfSigned(at.AddMinutes(-5), at + Validity);
        var pfx = created.Export(X509ContentType.Pfx);
        WriteOwnerOnly(path, pfx);
        logger?.LogInformation("Created storage TLS certificate {Fingerprint} at {Path}.", StorageTlsProof.Fingerprint(created.RawData), path);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }

    /// <summary>The private key is never readable by others, even briefly, and a crash can't leave a truncated file in place.</summary>
    private static void WriteOwnerOnly(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>The current certificate and its published identity binding; swapped as a whole on renewal.</summary>
public sealed record StorageTlsSnapshot(X509Certificate2 Certificate, string Fingerprint, long NotAfterUnixSeconds, string Proof);

/// <summary>
/// Holds the gRPC port's certificate (issue #142, review): loaded at startup once the node identity is
/// available, re-checked daily and renewed in place before expiry. Kestrel's certificate selector and
/// <c>/v1/node/services</c> both read <see cref="Current"/>, so a renewal takes effect for new TLS
/// sessions and is published in one step; gateways re-pin on their next registry refresh.
/// </summary>
public sealed class StorageTlsCertificateProvider(
    NodeIdentity identity, IOptions<StorageNodeOptions> options, ILogger<StorageTlsCertificateProvider> logger) : BackgroundService
{
    private StorageTlsSnapshot? _current;

    public StorageTlsSnapshot Current =>
        Volatile.Read(ref _current) ?? throw new InvalidOperationException("The storage TLS certificate hasn't been loaded; call EnsureLoaded() at startup.");

    public string Path
    {
        get
        {
            var o = options.Value;
            return string.IsNullOrWhiteSpace(o.TlsCertificatePath)
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(o.ManifestDbPath))!, "grpc-tls.pfx")
                : o.TlsCertificatePath;
        }
    }

    public void EnsureLoaded(DateTimeOffset? now = null)
    {
        identity.EnsureLoadedOrGenerated();
        var certificate = StorageTlsCertificate.LoadOrCreate(Path, identity.NodeId, logger, now);
        var fingerprint = StorageTlsProof.Fingerprint(certificate.RawData);
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds();
        var previous = Interlocked.Exchange(ref _current,
            new StorageTlsSnapshot(certificate, fingerprint, notAfter, identity.SignStorageTls(Convert.FromHexString(fingerprint), notAfter)));
        if (previous is not null && previous.Fingerprint != fingerprint)
        {
            logger.LogInformation("Storage TLS certificate renewed: now {Fingerprint}, valid until {NotAfter}.", fingerprint, certificate.NotAfter);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                EnsureLoaded();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Storage TLS certificate renewal check failed; keeping the current certificate.");
            }
        }
    }
}
