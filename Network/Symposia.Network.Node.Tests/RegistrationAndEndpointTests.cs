using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Symposia.Network.Node.Tests;

/// <summary>
/// Registration sync (#129 FR-C3) and the handshake HTTP surface (#129 FR-D1/FR-D4).
/// </summary>
public sealed class RegistrationAndEndpointTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "symposia-network-endpoints-" + Guid.NewGuid());
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:NodeIdentityKeyPath"] = Path.Combine(_dataDir, "node.key"),
            ["Node:NodeRegistryAddress"] = TestSupport.Registry,
            ["Node:ChainId"] = TestSupport.ChainId.ToString(),
            ["Node:HandshakeRateLimitPerMinute"] = "3",
        });
        builder.Services.AddNodeNetwork(builder.Configuration.GetSection("Node"));
        _app = builder.Build();
        _app.UseNodeNetwork();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    private NodeIdentity ServerIdentity => _app.Services.GetRequiredService<NodeIdentity>();

    // FR-D1 end to end over HTTP: the response verifies against the server's address.
    [Fact]
    public async Task Handshake_ReturnsSignatureRecoveringToNodeAddress()
    {
        var dialer = TestSupport.NewIdentity(_dataDir).NodeId;
        var nonce = new byte[PeerHandshake.NonceLength];
        Random.Shared.NextBytes(nonce);

        var resp = await _client.PostAsJsonAsync("/v1/peer/handshake",
            new { dialer, nonce = "0x" + Convert.ToHexString(nonce) });
        var body = await resp.Content.ReadFromJsonAsync<HandshakeResponse>();

        Assert.Equal(ServerIdentity.NodeId, body!.Address);
        var recovered = PeerHandshake.Recover(TestSupport.ChainId, TestSupport.Registry, dialer, body.Address, nonce, body.Signature);
        Assert.Equal(ServerIdentity.NodeId, recovered, ignoreCase: true);
    }

    [Theory]
    [InlineData("not-an-address", "0x" + "00000000000000000000000000000000000000000000000000000000000000AA")]
    [InlineData("0x5FbDB2315678afecb367f032d93F642f64180aa3", "0x1234")]
    [InlineData("0x5FbDB2315678afecb367f032d93F642f64180aa3", "zz")]
    [InlineData("0x5FbDB2315678afecb367f032d93F642f64180aa3", null)]
    [InlineData(null, "0x" + "00000000000000000000000000000000000000000000000000000000000000AA")]
    public async Task Handshake_MalformedRequest_IsBadRequest(string? dialer, string? nonce)
    {
        var resp = await _client.PostAsJsonAsync("/v1/peer/handshake", new { dialer, nonce });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Peers_ListsNodeAndPeerTable()
    {
        _app.Services.GetRequiredService<PeerTable>().Replace([new PeerEntry(
            "http://b:5180", "0xabc", "0xabc", PeerStatus.Recognized, PeerFailureReason.None, DateTimeOffset.UtcNow)]);

        var body = await _client.GetFromJsonAsync<JsonElement>("/v1/peers");

        Assert.Equal(ServerIdentity.NodeId, body.GetProperty("node").GetString());
        Assert.Equal("Recognized", body.GetProperty("peers")[0].GetProperty("status").GetString());
    }

    // FR-C3: registers on first start, no-ops when unchanged, re-signs with next nonce on change.
    [Fact]
    public async Task RegistrationSync_RegistersThenUpdatesEndpointWithNextNonce()
    {
        var registry = new FakeRegistry();
        var identity = TestSupport.NewIdentity(_dataDir);
        var options = TestSupport.Options("unused");
        options.AdvertisedEndpoint = "http://a:5180";
        var service = new NodeRegistrationHostedService(identity, registry, Options.Create(options), NullLogger<NodeRegistrationHostedService>.Instance);

        await service.RegisterOnceAsync(CancellationToken.None);
        options.AdvertisedEndpoint = "HTTP://A:5180/";
        await service.RegisterOnceAsync(CancellationToken.None);
        options.AdvertisedEndpoint = "http://a2:5180";
        await service.RegisterOnceAsync(CancellationToken.None);

        Assert.Equal(
            [(identity.NodeId, "http://a:5180", 0UL), (identity.NodeId, "http://a2:5180", 1UL)],
            registry.Registrations);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }
}
