using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// #228: on a gateway-only host the gateway is the node's primary endpoint, so it answers the peer handshake (signed
/// with the node key), and SigV4 lets exactly the primary paths through, nothing else under /v1.
/// </summary>
public sealed class GatewayPrimaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym228-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private WebApplicationFactory<Program> Gateway(bool primary)
    {
        Directory.CreateDirectory(_dir);
        var keyPath = Path.Combine(_dir, "node.key");
        new NodeIdentity(Microsoft.Extensions.Options.Options.Create(new NodeNetworkOptions { NodeIdentityKeyPath = keyPath })).EnsureLoadedOrGenerated();
        // UseSetting, not ConfigureAppConfiguration: BlobGatewayApp reads these while building.
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["Gateway:MetadataDbPath"] = Path.Combine(_dir, "gateway-metadata.db"),
                ["Gateway:ServesPrimary"] = primary.ToString(),
                ["GatewayNode:NodeIdentityKeyPath"] = keyPath,
                ["GatewayNode:EnableRegistration"] = "false",
                ["GatewayNode:EnableDiscovery"] = "false",
                ["GatewayNode:ChainId"] = "31337",
                ["GatewayNode:NodeRegistryAddress"] = "0x5FbDB2315678afecb367f032d93F642f64180aa3",
            })
            {
                b.UseSetting(key, value);
            }
        });
    }

    private static HandshakeRequestBody Handshake() =>
        new(Nethereum.Signer.EthECKey.GenerateKey().GetPublicAddress(), "0x" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(PeerHandshake.NonceLength)));

    private sealed record HandshakeRequestBody(string Dialer, string Nonce);

    [Fact]
    public async Task Primary_AnswersTheHandshake_WithTheNodeKey()
    {
        await using var gateway = Gateway(primary: true);

        var response = await gateway.CreateClient().PostAsJsonAsync(PrimaryEndpointPaths.Handshake, Handshake());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HandshakeResponse>();
        var keyPath = Path.Combine(_dir, "node.key");
        var identity = new NodeIdentity(Microsoft.Extensions.Options.Options.Create(new NodeNetworkOptions { NodeIdentityKeyPath = keyPath }));
        identity.LoadExisting();
        Assert.Equal(identity.NodeId, body!.Address, ignoreCase: true);
    }

    [Fact]
    public async Task Primary_BypassIsExact_TheRestOfV1StillNeedsSigV4()
    {
        await using var gateway = Gateway(primary: true);
        var client = gateway.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/buckets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/peer/handshake/extra")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/peers")).StatusCode);
    }

    [Fact]
    public async Task NotPrimary_ServesNoHandshake()
    {
        await using var gateway = Gateway(primary: false);

        var response = await gateway.CreateClient().PostAsJsonAsync(PrimaryEndpointPaths.Handshake, Handshake());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // SigV4 as for any /v1 path (AC3)
    }

    [Theory]
    [InlineData("/v1/peer/handshake", true)]
    [InlineData("/v1/node/health", true)]
    [InlineData("/.well-known/symposia/services", true)]
    [InlineData("/v1/peer/handshake/", false)]
    [InlineData("/v1/node", false)]
    [InlineData("/v1/buckets", false)]
    public void PrimaryPaths_AreExactMatches(string path, bool expected) => Assert.Equal(expected, PrimaryEndpointPaths.Contains(path));
}
