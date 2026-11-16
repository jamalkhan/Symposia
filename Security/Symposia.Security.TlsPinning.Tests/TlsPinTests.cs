using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Symposia.Security.TlsPinning.Tests;

/// <summary>The shared pinning rules (#252; QA plan TC2, TC4).</summary>
public sealed class TlsPinTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static X509Certificate2 Cert(DateTimeOffset notBefore, DateTimeOffset notAfter, params string[] usages)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=pin-test", key, HashAlgorithmName.SHA256);
        if (usages.Length > 0)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([.. usages.Select(u => new Oid(u))], false));
        }

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static X509Certificate2 Valid(params string[] usages) => Cert(Now.AddMinutes(-5), Now.AddDays(1), usages);

    [Fact]
    public void GoldenFingerprint_IsTheWireFormat_AndAPreviousReleaseCertificatePassesTheNewChecks() // AC3, TC4.1, TC5.2
    {
        // Minted by the #142 release's own StorageTlsCertificate.LoadOrCreate (see the fixture's description).
        const string Golden = "96cce2d7939658c4b3cbf21a3b639ab8d688b5bad832960337529bd06b257569";
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "0.1.0-node-certificate.json")));
        var der = Convert.FromBase64String(fixture.RootElement.GetProperty("certificateDer").GetString()!);
        var at = DateTimeOffset.FromUnixTimeSeconds(fixture.RootElement.GetProperty("notBeforeUnixSeconds").GetInt64() + 3600);
        using var cert = X509CertificateLoader.LoadCertificate(der);

        Assert.Equal(Golden, fixture.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal(Golden, TlsPin.Fingerprint(der));
        Assert.Equal(Golden, TlsPin.Fingerprint(cert));
        Assert.Equal(Golden, Convert.ToHexStringLower(SHA256.HashData(der))); // the #142 formula, spelled out
        Assert.Equal(TlsPinResult.Ok, TlsPin.Check(cert, [Golden], at, TlsPin.ServerAuth));
    }

    [Fact]
    public void EveryResult_HasADistinctTag() // review: Carl #2, Frank #4
    {
        var tags = Enum.GetValues<TlsPinResult>().Select(r => r.ToTag()).ToList();

        Assert.Equal(tags.Count, tags.Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((TlsPinResult)99).ToTag());
    }

    [Theory]
    [InlineData("AB:CD")]
    [InlineData("")]
    [InlineData("0x7ea34503ae92371d13c92a80dc300cb9c57ea7996259f28e9adaa161bcda3e9")]
    [InlineData("7ea34503ae92371d13c92a80dc300cb9c57ea7996259f28e9adaa161bcda3e9z")]
    [InlineData("7ea34503ae92371d13c92a80dc300cb9c57ea7996259f28e9adaa161bcda3e955")]
    public void Normalize_RejectsAnythingButSixtyFourHexDigits(string pin) // TC2.3
    {
        Assert.Throws<FormatException>(() => TlsPin.Normalize(pin));
        Assert.False(TlsPin.TryNormalize(pin, out _));
    }

    [Fact]
    public void Normalize_AcceptsOpensslAndUpperCase_AndNullIsAnArgumentError() // TC2.2, TC2.4
    {
        using var cert = Valid();
        var fingerprint = TlsPin.Fingerprint(cert);
        var openssl = string.Join(':', Enumerable.Range(0, 32).Select(i => fingerprint.Substring(i * 2, 2))).ToUpperInvariant();

        Assert.Equal(fingerprint, TlsPin.Normalize(openssl));
        Assert.Equal(fingerprint, TlsPin.Normalize($"  {fingerprint.ToUpperInvariant()} "));
        Assert.Throws<ArgumentNullException>(() => TlsPin.Normalize(null!));
        Assert.False(TlsPin.TryNormalize(null, out _));
    }

    [Fact]
    public void Check_NamesWhyACertificateIsRefused() // TC2.5, TC2.6
    {
        using var cert = Valid();
        var pin = TlsPin.Fingerprint(cert);

        Assert.Equal(TlsPinResult.Ok, TlsPin.Check(cert, [pin], Now));
        Assert.Equal(TlsPinResult.NoCertificate, TlsPin.Check(null, [pin], Now));
        Assert.Equal(TlsPinResult.FingerprintMismatch, TlsPin.Check(cert, [], Now));
        Assert.Equal(TlsPinResult.FingerprintMismatch, TlsPin.Check(cert, [pin.ToUpperInvariant()], Now)); // callers normalise
    }

    [Theory]
    [InlineData(3, TlsPinResult.Ok)]
    [InlineData(5, TlsPinResult.Ok)] // the tolerance is inclusive
    [InlineData(6, TlsPinResult.NotYetValid)]
    [InlineData(24 * 60, TlsPinResult.NotYetValid)]
    public void NotBefore_AllowsFiveMinutesOfClockSkew(int minutesAhead, TlsPinResult expected) // TC2.8, Gherkin
    {
        using var cert = Cert(Now.AddMinutes(minutesAhead), Now.AddDays(1));

        Assert.Equal(expected, TlsPin.Check(cert, [TlsPin.Fingerprint(cert)], Now));
    }

    [Fact]
    public void ClockSkew_IsConfigurable_AndNeverNegative() // TC2.14
    {
        using var cert = Cert(Now.AddMinutes(3), Now.AddDays(1));
        var pins = new[] { TlsPin.Fingerprint(cert) };

        Assert.Equal(TlsPinResult.NotYetValid, TlsPin.Check(cert, pins, Now, clockSkew: TimeSpan.FromMinutes(1)));
        Assert.Equal(TlsPinResult.Ok, TlsPin.Check(cert, pins, Now, clockSkew: TimeSpan.FromMinutes(10)));
        Assert.Equal(TlsPinResult.NotYetValid, TlsPin.Check(cert, pins, Now, clockSkew: TimeSpan.FromMinutes(-10)));
    }

    [Fact]
    public void NotAfter_HasNoTolerance() // TC2.7: expiry is the storage proof's replay boundary
    {
        // X.509 time has whole-second precision.
        using var cert = Cert(Now.AddDays(-1), Now.AddSeconds(1));
        var pins = new[] { TlsPin.Fingerprint(cert) };

        Assert.Equal(TlsPinResult.Ok, TlsPin.Check(cert, pins, Now));
        Assert.Equal(TlsPinResult.Expired, TlsPin.Check(cert, pins, Now.AddSeconds(1)));
        Assert.Equal(TlsPinResult.Expired, TlsPin.Check(cert, pins, Now.AddSeconds(2)));
    }

    [Fact]
    public void Usage_IsRequiredOnlyWhenTheCertificateDeclaresUsages() // TC2.9, TC2.10 (RFC 5280)
    {
        using var server = Valid(TlsPin.ServerAuth);
        using var client = Valid(TlsPin.ClientAuth);
        using var both = Valid(TlsPin.ServerAuth, TlsPin.ClientAuth);
        using var none = Valid();

        Assert.Equal(TlsPinResult.Ok, TlsPin.Check(server, [TlsPin.Fingerprint(server)], Now, TlsPin.ServerAuth));
        Assert.Equal(TlsPinResult.MissingUsage, TlsPin.Check(client, [TlsPin.Fingerprint(client)], Now, TlsPin.ServerAuth));
        Assert.Equal(TlsPinResult.Ok, TlsPin.Check(both, [TlsPin.Fingerprint(both)], Now, TlsPin.ServerAuth));
        Assert.Equal(TlsPinResult.Ok, TlsPin.Check(none, [TlsPin.Fingerprint(none)], Now, TlsPin.ServerAuth));
        Assert.Equal(TlsPinResult.Ok, TlsPin.Check(client, [TlsPin.Fingerprint(client)], Now));
    }

    [Fact]
    public void APlainX509Certificate_IsStillCheckedForValidityAndUsage() // TC2.11: fails closed, not open
    {
        using var expired = Cert(Now.AddDays(-2), Now.AddDays(-1));
        using var clientOnly = Valid(TlsPin.ClientAuth);
#pragma warning disable SYSLIB0026, SYSLIB0057 // deliberately the base type, as SslStream callbacks hand it over
        using var plainExpired = new X509Certificate(expired.RawData);
        using var plainClient = new X509Certificate(clientOnly.RawData);
#pragma warning restore SYSLIB0026, SYSLIB0057

        Assert.Equal(TlsPinResult.Expired, TlsPin.Check(plainExpired, [TlsPin.Fingerprint(expired)], Now));
        Assert.Equal(TlsPinResult.MissingUsage, TlsPin.Check(plainClient, [TlsPin.Fingerprint(clientOnly)], Now, TlsPin.ServerAuth));
    }

    [Fact]
    public void LoadCertificate_ReadsPemAndDer_WithoutAKey() // TC2.13
    {
        using var cert = Valid();
        var dir = Directory.CreateTempSubdirectory("tlspin-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "c.pem"), cert.ExportCertificatePem());
            File.WriteAllBytes(Path.Combine(dir, "c.der"), cert.RawData);

            using var pem = TlsPin.LoadCertificate(Path.Combine(dir, "c.pem"));
            using var der = TlsPin.LoadCertificate(Path.Combine(dir, "c.der"));
            Assert.Equal(TlsPin.Fingerprint(cert), TlsPin.Fingerprint(pem));
            Assert.Equal(TlsPin.Fingerprint(cert), TlsPin.Fingerprint(der));
            Assert.False(pem.HasPrivateKey);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_ReadsAPfx_AndAPemWithItsKey_WithTheKeyUsable() // TC2.12
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = new CertificateRequest("CN=load", key, HashAlgorithmName.SHA256).CreateSelfSigned(Now.AddDays(-1), Now.AddDays(1));
        var dir = Directory.CreateTempSubdirectory("tlspin-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "c.pfx"), cert.Export(X509ContentType.Pkcs12, "pw"));
            File.WriteAllText(Path.Combine(dir, "c.pem"), cert.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem());

            using var pfx = TlsPin.Load(Path.Combine(dir, "c.pfx"), password: "pw");
            using var pem = TlsPin.Load(Path.Combine(dir, "c.pem"));
            Assert.True(pfx.HasPrivateKey);
            Assert.True(pem.HasPrivateKey);
            Assert.ThrowsAny<CryptographicException>(() => TlsPin.Load(Path.Combine(dir, "c.pfx"), password: "wrong"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheAssembly_ReferencesOnlyTheBaseClassLibrary() // TC1.3, FR1
    {
        var references = typeof(TlsPin).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.All(references, name => Assert.True(name.StartsWith("System") || name == "netstandard" || name == "mscorlib", name));
    }
}
