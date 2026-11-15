extern alias GatewayAlias;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Symposia.BlobStorage.CredentialAuthority;
using Symposia.BlobStorage.Domain;

namespace Symposia.BlobStorage.CredentialAuthority.Tests;

/// <summary>
/// Mutual TLS on the internal API (issue #245 FR6, AC6), over real Kestrel listeners: TestServer skips the TLS
/// handshake, so a pass there would prove nothing about the certificate checks.
/// </summary>
public sealed class MutualTlsTests(AuthorityFixture f) : IClassFixture<AuthorityFixture>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sym245-tls-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Pfx(string name, out X509Certificate2 certificate)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(_dir, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));
        return path;
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private sealed record Running(WebApplication App, int InternalPort, int OperatorPort, X509Certificate2 Server) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private async Task<Running> StartAsync(string mode, params string[] allowed)
    {
        var serverPath = Pfx("authority", out var server);
        var (internalPort, operatorPort) = (FreePort(), FreePort());
        var app = await AuthorityApp.CreateAsync(new WebApplicationOptions { EnvironmentName = "Production" }, b =>
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CredentialAuthority:ConnectionString"] = f.ConnectionString,
                ["CredentialAuthority:ServiceToken"] = AuthorityFixture.ServiceToken,
                ["CredentialAuthority:OperatorToken"] = AuthorityFixture.OperatorToken,
                ["CredentialAuthority:MasterKey"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()),
                ["CredentialAuthority:InternalTls:Mode"] = mode,
                ["CredentialAuthority:InternalTls:Port"] = internalPort.ToString(),
                ["CredentialAuthority:InternalTls:OperatorPort"] = operatorPort.ToString(),
                ["CredentialAuthority:InternalTls:CertificatePath"] = serverPath,
            }.Concat(allowed.Select((pin, i) => KeyValuePair.Create($"CredentialAuthority:InternalTls:ClientFingerprints:{i}", (string?)pin)))));
        await app.StartAsync();
        return new Running(app, internalPort, operatorPort, server);
    }

    /// <summary>A client presenting <paramref name="clientPath"/> (or nothing), trusting the server only by its pin.</summary>
    private static HttpClient Client(int port, X509Certificate2 server, string? clientPath, bool token = true)
    {
        var handler = GatewayAlias::Symposia.BlobStorage.Gateway.Auth.AuthorityTls.Handler(new GatewayAlias::Symposia.BlobStorage.Gateway.CredentialAuthorityOptions
        {
            ClientCertificatePath = clientPath ?? "",
            ServerFingerprints = [TlsPin.Fingerprint(server)],
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{port}/") };
        if (token) http.DefaultRequestHeaders.Add("X-Service-Token", AuthorityFixture.ServiceToken);
        return http;
    }

    [Fact]
    public async Task Enforced_AllowlistedCertificateAndToken_Succeed_AndEveryMissingFactorFails()
    {
        var goodPath = Pfx("gateway", out var good);
        var strangerPath = Pfx("stranger", out _);
        await using var authority = await StartAsync("Enforced", TlsPin.Fingerprint(good));

        using (var ok = Client(authority.InternalPort, authority.Server, goodPath))
        {
            Assert.Equal(HttpStatusCode.OK, (await ok.GetAsync("internal/snapshot")).StatusCode);
        }

        using (var noToken = Client(authority.InternalPort, authority.Server, goodPath, token: false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await noToken.GetAsync("internal/snapshot")).StatusCode);
        }

        // No certificate, or one not on the allowlist: the handshake itself fails, before any handler runs.
        using (var anonymous = Client(authority.InternalPort, authority.Server, null))
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => anonymous.GetAsync("internal/snapshot"));
        }

        using (var stranger = Client(authority.InternalPort, authority.Server, strangerPath))
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => stranger.GetAsync("internal/snapshot"));
        }
    }

    [Fact]
    public async Task InternalApi_IsNeverServedOnTheOperatorListener()
    {
        await using var authority = await StartAsync("Enforced");
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{authority.OperatorPort}/") };
        http.DefaultRequestHeaders.Add("X-Service-Token", AuthorityFixture.ServiceToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("internal/snapshot")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("health")).StatusCode); // operator side unaffected
    }

    [Fact]
    public async Task Permissive_AcceptsAndCountsRequestsWithoutACertificate_ButStillRejectsAStranger()
    {
        var strangerPath = Pfx("stranger", out _);
        await using var authority = await StartAsync("Permissive");
        var tls = authority.App.Services.GetRequiredService<InternalTls>();

        using (var anonymous = Client(authority.InternalPort, authority.Server, null))
        {
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("internal/snapshot")).StatusCode);
        }

        Assert.Equal(1, tls.RequestsWithoutCertificate);
        using var stranger = Client(authority.InternalPort, authority.Server, strangerPath);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => stranger.GetAsync("internal/snapshot"));
    }

    [Fact]
    public async Task Gateway_RefusesAnAuthorityWhoseCertificateDoesNotMatchItsPin()
    {
        var goodPath = Pfx("gateway", out var good);
        await using var authority = await StartAsync("Enforced", TlsPin.Fingerprint(good));
        Pfx("impostor", out var impostor);

        using var pinnedElsewhere = Client(authority.InternalPort, impostor, goodPath);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => pinnedElsewhere.GetAsync("internal/snapshot"));
    }
}
