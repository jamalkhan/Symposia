using System.Text.Json;
using Symposia.BlobStorage.Domain.Credentials;
using Symposia.BlobStorage.Gateway.Auth;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// The gateway parses every golden Authority snapshot and delta in protocol/fixtures (#245; rule 1: work with the
/// previous release's shapes). The fixtures are history: new shapes get new fixtures, old ones stay.
/// </summary>
public sealed class AuthorityWireFixtureTests
{
    private static string Fixtures(string kind)
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "protocol", "fixtures"))) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "protocol", "fixtures", kind);
    }

    [Fact]
    public void EverySnapshotFixture_Parses()
    {
        foreach (var file in Directory.GetFiles(Fixtures("authority-snapshot"), "*.json"))
        {
            var snapshot = JsonSerializer.Deserialize<AuthoritySnapshot>(File.ReadAllText(file), CredentialSync.Json)!;
            Assert.NotEmpty(snapshot.Credentials);
            Assert.NotNull(snapshot.Credentials[0].Secret);
        }
    }

    [Fact]
    public void EveryDeltaFixture_Parses_AndAnUnchangedSecretIsNull()
    {
        foreach (var file in Directory.GetFiles(Fixtures("authority-delta"), "*.json"))
        {
            var delta = JsonSerializer.Deserialize<AuthorityDelta>(File.ReadAllText(file), CredentialSync.Json)!;
            Assert.True(delta.Version > delta.FromVersion);
            Assert.All(delta.Credentials, c => Assert.Null(c.Secret));
        }
    }
}

/// <summary>One fingerprint format for every pin in the system (#245, #142): runbooks produce one kind of pin.</summary>
public sealed class TlsPinTests
{
    [Fact]
    public void AuthorityPins_AndStorageNodePins_AreTheSameFingerprint()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        using var cert = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=x", key, System.Security.Cryptography.HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(Symposia.Network.Node.StorageTlsProof.Fingerprint(cert.RawData), Symposia.BlobStorage.Domain.TlsPin.Fingerprint(cert));
    }
}
