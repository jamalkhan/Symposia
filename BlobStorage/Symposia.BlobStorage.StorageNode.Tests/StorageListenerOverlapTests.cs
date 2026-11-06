using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Symposia.BlobStorage.Protocol;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>
/// Issue #145: during the overlap a storage node serves gRPC over TLS and in cleartext, and its
/// services document advertises both with its protocol version and features (QA TC-15..TC-17, TC-20).
/// </summary>
public sealed class StorageListenerOverlapTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym145n-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _apps = [];

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private async Task<(int Http, NodeServices Services)> StartAsync(int grpcPort, int grpcTlsPort)
    {
        var http = FreePort();
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        var app = StorageNodeApp.Create(new WebApplicationOptions { EnvironmentName = "Testing" }, b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StorageNode:ListenPort"] = http.ToString(),
            ["StorageNode:GrpcPort"] = grpcPort.ToString(),
            ["StorageNode:GrpcTlsPort"] = grpcTlsPort.ToString(),
            ["StorageNode:StorageRoot"] = Path.Combine(dir, "blobs"),
            ["StorageNode:ManifestDbPath"] = Path.Combine(dir, "manifest.db"),
            ["StorageNode:NodeIdentityKeyPath"] = Path.Combine(dir, "node.key"),
            ["StorageNode:AdvertisedEndpoint"] = $"http://127.0.0.1:{http}",
            ["StorageNode:EnableRegistration"] = "false",
            ["StorageNode:EnableDiscovery"] = "false",
            // The TLS proof binds the certificate to a network (#142).
            ["StorageNode:ChainId"] = "31337",
            ["StorageNode:NodeRegistryAddress"] = "0x5FbDB2315678afecb367f032d93F642f64180aa3",
        }));
        await app.StartAsync();
        _apps.Add(app);

        using var client = new HttpClient();
        var services = (await client.GetFromJsonAsync<NodeServices>($"http://127.0.0.1:{http}{NodeServices.Path}", JsonSerializerOptions.Web))!;
        return (http, services);
    }

    private static async Task<string> ProbeAsync(string url, string? pin)
    {
        var handler = new SocketsHttpHandler();
        if (pin is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert is not null && StorageTlsProof.PinMatches(cert.GetRawCertData(), DateTimeOffset.UtcNow.AddDays(1), pin, DateTimeOffset.UtcNow);
        }

        using var channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions { HttpHandler = handler });
        var reply = await new Symposia.BlobStorage.Protocol.StorageNode.StorageNodeClient(channel).ProbeAsync(new ProbeRequest());
        return reply.NodeId;
    }

    [Fact]
    public async Task TlsAndCleartext_BothServe_AndBothAreAdvertised()
    {
        var (grpc, tls) = (FreePort(), FreePort());
        var (_, services) = await StartAsync(grpc, tls);

        Assert.Equal(ProtocolFeatures.ProtocolVersion, services.ProtocolVersion);
        Assert.Contains(ProtocolFeatures.StorageGrpcTls, services.Features!);
        Assert.Contains(ProtocolFeatures.StorageGrpcPlaintext, services.Features!);
        Assert.Equal($"http://127.0.0.1:{grpc}", services.Storage!.Grpc);
        Assert.Equal($"https://127.0.0.1:{tls}", services.Storage.GrpcTls);

        Assert.Equal(services.Node, await ProbeAsync(services.Storage.GrpcTls!, services.Storage.TlsFingerprint));
        Assert.Equal(services.Node, await ProbeAsync(services.Storage.Grpc!, pin: null));
    }

    [Fact]
    public async Task TlsOnly_AdvertisesNoCleartext()
    {
        var (_, services) = await StartAsync(grpcPort: 0, grpcTlsPort: FreePort());
        Assert.Null(services.Storage!.Grpc);
        Assert.DoesNotContain(ProtocolFeatures.StorageGrpcPlaintext, services.Features!);
        Assert.Contains(ProtocolFeatures.StorageGrpcTls, services.Features!);
    }

    [Fact]
    public async Task CleartextOnly_AdvertisesNoTls()
    {
        var (_, services) = await StartAsync(grpcPort: FreePort(), grpcTlsPort: 0);
        Assert.Null(services.Storage!.GrpcTls);
        Assert.Null(services.Storage.TlsFingerprint);
        Assert.Equal([ProtocolFeatures.StorageGrpcPlaintext], services.Features!);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
