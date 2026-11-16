extern alias NodeAlias;

using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.Security.TlsPinning;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// Gateway→storage-node pinning over a real TLS handshake (#252 AC4): NotBefore with clock skew and the serverAuth
/// usage are now checked, refusals are reported with their reason, and certificates the node itself creates pass.
/// </summary>
public sealed class NodeTlsHandshakeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sym252-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Recorder : ITlsRefusalSink
    {
        public List<(string Node, TlsPinResult Reason)> Refusals { get; } = [];

        public void Refused(string nodeId, string url, TlsPinResult reason, string? presentedFingerprint)
        {
            lock (Refusals) Refusals.Add((nodeId, reason));
        }
    }

    private static X509Certificate2 Cert(TimeSpan notBeforeFromNow, params string[] usages)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=node", key, HashAlgorithmName.SHA256);
        if (usages.Length > 0)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([.. usages.Select(u => new Oid(u))], false));
        }

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow + notBeforeFromNow, DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>What the gateway side saw: whether its certificate check passed, and the refusal it recorded.</summary>
    private sealed record Outcome(bool ClientAccepted, string? TlsRefusal);

    /// <summary>
    /// Serves TLS with <paramref name="certificate"/> and dials it through a pinned NodeConnection. Under TLS 1.3 the
    /// server finishes its side before the client checks the certificate, so success is judged on the client: the
    /// server waits for the HTTP/2 preface, which a client only sends once it has accepted the certificate (review:
    /// Lenny #2). What follows isn't gRPC, so the call itself is expected to fail either way.
    /// </summary>
    private static async Task<Outcome> HandshakeAsync(X509Certificate2 certificate, ITlsRefusalSink sink, string? pin = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var tls = new SslStream(client.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http2],
                });
                var preface = new byte[24];
                return await tls.ReadAtLeastAsync(preface, preface.Length, throwOnEndOfStream: false) == preface.Length
                    && preface.AsSpan().SequenceEqual("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                return false;
            }
        });

        using var node = new NodeConnection($"https://127.0.0.1:{port}", "0xnode", tlsFingerprint: pin ?? TlsPin.Fingerprint(certificate), refusals: sink);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await node.Client.ProbeAsync(new Protocol.ProbeRequest(), cancellationToken: cts.Token);
        }
        catch (Grpc.Core.RpcException)
        {
        }

        return new Outcome(await server.WaitAsync(TimeSpan.FromSeconds(10)), node.LastTlsRefusal);
    }

    [Fact]
    public async Task TheCertificateANodeCreates_IsTrusted() // AC4 positive, Gherkin "previous-release node"
    {
        using var created = NodeAlias::Symposia.BlobStorage.StorageNode.Grpc.StorageTlsCertificate.LoadOrCreate(Path.Combine(_dir, "node.pfx"), "0xnode");
        var sink = new Recorder();

        Assert.Equal(new Outcome(true, null), await HandshakeAsync(created, sink));
        Assert.Empty(sink.Refusals);
    }

    [Fact]
    public async Task ANodeClockThreeMinutesAhead_IsTrusted() // Gherkin "Node clock slightly ahead"
    {
        using var cert = Cert(TimeSpan.FromMinutes(3), TlsPin.ServerAuth);
        var sink = new Recorder();

        Assert.Equal(new Outcome(true, null), await HandshakeAsync(cert, sink));
        Assert.Empty(sink.Refusals);
    }

    [Theory]
    [InlineData(10, TlsPin.ServerAuth, TlsPinResult.NotYetValid)]
    [InlineData(-5, TlsPin.ClientAuth, TlsPinResult.MissingUsage)] // Gherkin "without server-auth usage"
    public async Task ACertificateFailingTheNewChecks_IsRefused_WithItsReason(int notBeforeMinutes, string usage, TlsPinResult reason) // AC4
    {
        using var cert = Cert(TimeSpan.FromMinutes(notBeforeMinutes), usage);
        var sink = new Recorder();

        Assert.Equal(new Outcome(false, "tls: " + reason.ToTag()), await HandshakeAsync(cert, sink));
        Assert.Equal([("0xnode", reason)], sink.Refusals);
    }

    [Fact]
    public async Task AnUnpinnedCertificate_IsRefusedAsAMismatch() // TC5.6
    {
        using var cert = Cert(TimeSpan.FromMinutes(-5), TlsPin.ServerAuth);
        using var other = Cert(TimeSpan.FromMinutes(-5), TlsPin.ServerAuth);
        var sink = new Recorder();

        Assert.Equal(new Outcome(false, "tls: fingerprint_mismatch"), await HandshakeAsync(cert, sink, pin: TlsPin.Fingerprint(other)));
        Assert.Equal([("0xnode", TlsPinResult.FingerprintMismatch)], sink.Refusals);
    }

    [Fact]
    public async Task AnUpperCaseRegistryPin_StillMatches() // TC4.4: the old node match ignored case
    {
        using var cert = Cert(TimeSpan.FromMinutes(-5), TlsPin.ServerAuth);
        var sink = new Recorder();

        Assert.Equal(new Outcome(true, null), await HandshakeAsync(cert, sink, pin: TlsPin.Fingerprint(cert).ToUpperInvariant()));
        Assert.Empty(sink.Refusals);
    }

    private sealed class ThrowingSink : ITlsRefusalSink
    {
        public void Refused(string nodeId, string url, TlsPinResult reason, string? presentedFingerprint) => throw new InvalidOperationException("meter down");
    }

    [Fact]
    public async Task AFaultingSink_NeitherFlipsTheOutcome_NorLosesTheReason() // review: Lenny #7
    {
        using var cert = Cert(TimeSpan.FromMinutes(-5), TlsPin.ClientAuth);

        Assert.Equal(new Outcome(false, "tls: missing_usage"), await HandshakeAsync(cert, new ThrowingSink()));
    }

    [Fact]
    public async Task TheRegistrysProbeError_LeadsWithTheTlsReason() // review: Lenny #1, through NodeRegistry's own probe
    {
        using var cert = Cert(TimeSpan.FromMinutes(-5), TlsPin.ClientAuth);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using (client)
                    await using (var tls = new SslStream(client.GetStream()))
                    {
                        try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, ApplicationProtocols = [SslApplicationProtocol.Http2] }); }
                        catch (Exception) { }
                    }
                });
            }
        });
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions { MetadataDbPath = Path.Combine(_dir, "meta.db"), ProbeIntervalSeconds = 3600 });
        var store = new Metadata.GatewayMetadataStore(options);
        store.Initialize();
        using var nodes = new NodeRegistry(options, store, NullLogger<NodeRegistry>.Instance, new NetworkPolicy(options, NullLogger<NetworkPolicy>.Instance),
            tlsRefusals: new Recorder());
        nodes.Sync([new StorageNodeDescriptor("0xnode", $"https://127.0.0.1:{port}", TlsPin.Fingerprint(cert))]);

        await nodes.StartAsync(CancellationToken.None); // probes at once
        var node = nodes.All.Single();
        for (var i = 0; i < 100 && node.LastProbeError is null; i++) await Task.Delay(50);
        await nodes.StopAsync(CancellationToken.None);

        Assert.StartsWith("tls: missing_usage (", node.LastProbeError);
    }

    [Fact]
    public void Refusals_AreCountedByReason_AndLoggedOncePerNodeAndReason() // TC5.8, TC5.9
    {
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = services.GetRequiredService<IMeterFactory>();
        using var counted = new MetricCollector<long>(meters, "Symposia.BlobStorage.Gateway.Nodes", "storage_node_tls_refusals_total");
        var logger = new Microsoft.Extensions.Logging.Testing.FakeLogger<NodeTlsRefusals>();
        var refusals = new NodeTlsRefusals(logger, meters);

        refusals.Refused("0xa", "https://a", TlsPinResult.MissingUsage, "ff");
        refusals.Refused("0xa", "https://a", TlsPinResult.MissingUsage, "ff");
        refusals.Refused("0xb", "https://b", TlsPinResult.Expired, null);

        Assert.Equal(3, counted.GetMeasurementSnapshot().Count);
        Assert.All(counted.GetMeasurementSnapshot(), m => Assert.Equal(["reason"], m.Tags.Keys));
        Assert.Equal(2, logger.Collector.Count); // the repeat within five minutes isn't logged again
        Assert.Contains("0xa", logger.Collector.GetSnapshot()[0].Message);
    }
}
