using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Symposia.BlobStorage.StorageNode.Grpc;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>Issue #142: the gRPC TLS certificate is created once, reused, and renewed before expiry.</summary>
public sealed class StorageTlsCertificateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym142c-" + Guid.NewGuid().ToString("N"));
    private string PfxPath => Path.Combine(_dir, "grpc-tls.pfx");

    [Fact]
    public void Certificate_IsCreatedOwnerOnly_ThenReused()
    {
        var first = StorageTlsCertificate.LoadOrCreate(PfxPath, "0xabc");
        var again = StorageTlsCertificate.LoadOrCreate(PfxPath, "0xabc");

        Assert.Equal(StorageTlsCertificate.Fingerprint(first), StorageTlsCertificate.Fingerprint(again));
        Assert.True(first.HasPrivateKey);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PfxPath));
        }
    }

    [Fact]
    public void CertificateNearExpiry_IsRenewed()
    {
        Directory.CreateDirectory(_dir);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=old", key, HashAlgorithmName.SHA256);
        using var old = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-300), DateTimeOffset.UtcNow.AddDays(10));
        File.WriteAllBytes(PfxPath, old.Export(X509ContentType.Pfx));

        var renewed = StorageTlsCertificate.LoadOrCreate(PfxPath, "0xabc");

        Assert.NotEqual(StorageTlsCertificate.Fingerprint(old), StorageTlsCertificate.Fingerprint(renewed));
        Assert.True(renewed.NotAfter > DateTime.Now.AddDays(300));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
