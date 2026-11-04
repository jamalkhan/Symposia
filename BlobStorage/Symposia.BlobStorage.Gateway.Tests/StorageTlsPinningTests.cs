using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Issue #142: gateways pin storage certificates vouched for by the node's registry key.</summary>
public sealed class StorageTlsPinningTests : IDisposable
{
    private const ulong Chain = 31337;
    private const string Registry = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym142g-" + Guid.NewGuid().ToString("N"));

    private IOptions<GatewayOptions> Options(bool requireTls = true) => Microsoft.Extensions.Options.Options.Create(new GatewayOptions
    {
        MetadataDbPath = Path.Combine(_dir, "meta.db"),
        ProbeIntervalSeconds = 3600,
        ChainId = Chain,
        NodeRegistryAddress = Registry,
        RequireStorageTls = requireTls,
    });

    [Fact]
    public void PinMatches_OnlyTheExactCertificate()
    {
        var der = RandomNumberGenerator.GetBytes(64);
        var pin = Convert.ToHexStringLower(SHA256.HashData(der));
        Assert.True(NodeConnection.PinMatches(der, pin));
        Assert.True(NodeConnection.PinMatches(der, pin.ToUpperInvariant()));
        Assert.False(NodeConnection.PinMatches(RandomNumberGenerator.GetBytes(64), pin));
        Assert.False(NodeConnection.PinMatches(null, pin));
    }

    [Fact]
    public async Task Source_PinsVouchedCertificates_SkipsBadProofsAndCleartextWhenTlsRequired()
    {
        var good = EthECKey.GenerateKey();
        var forger = EthECKey.GenerateKey();
        var plain = EthECKey.GenerateKey();
        var fp = SHA256.HashData("cert"u8);
        var fpHex = Convert.ToHexStringLower(fp);
        var registry = new FakeRegistry(
            new RegisteredNode(good.GetPublicAddress(), "http://10.0.0.1:5180"),
            new RegisteredNode(forger.GetPublicAddress(), "http://10.0.0.2:5180"),
            new RegisteredNode(plain.GetPublicAddress(), "http://10.0.0.3:5180"));
        var docs = new Dictionary<string, NodeServices>
        {
            ["http://10.0.0.1:5180"] = new(good.GetPublicAddress(), new("https://10.0.0.1:6180", fpHex, StorageTlsProof.Sign(good, Chain, Registry, fp))),
            // Proof signed by a different key than the registered one.
            ["http://10.0.0.2:5180"] = new(forger.GetPublicAddress(), new("https://10.0.0.2:6180", fpHex, StorageTlsProof.Sign(good, Chain, Registry, fp))),
            ["http://10.0.0.3:5180"] = new(plain.GetPublicAddress(), new("http://10.0.0.3:6180")),
        };

        var required = await new RegistryNodeSource(registry, new Docs(docs), Options(), NullLogger<RegistryNodeSource>.Instance).DiscoverAsync(CancellationToken.None);
        var node = Assert.Single(required);
        Assert.Equal((good.GetPublicAddress(), fpHex), (node.Id, node.TlsFingerprint));

        var optional = await new RegistryNodeSource(registry, new Docs(docs), Options(requireTls: false), NullLogger<RegistryNodeSource>.Instance).DiscoverAsync(CancellationToken.None);
        Assert.Equal(2, optional.Count);
    }

    [Fact]
    public void RotatedCertificate_ReplacesTheConnection()
    {
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        using var nodes = new NodeRegistry(Options(), store, NullLogger<NodeRegistry>.Instance);
        nodes.Sync([new StorageNodeDescriptor("0xA", "https://10.0.0.1:6180", "aa")]);
        var before = Assert.Single(nodes.All);

        nodes.Sync([new StorageNodeDescriptor("0xA", "https://10.0.0.1:6180", "bb")]);

        var after = Assert.Single(nodes.All);
        Assert.NotSame(before, after);
        Assert.Equal("bb", after.TlsFingerprint);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeRegistry(params RegisteredNode[] nodes) : INodeRegistryClient
    {
        public Task<IReadOnlyList<RegisteredNode>> ListNodesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RegisteredNode>>(nodes);

        public Task<NodeRegistryStatus> GetStatusAsync(string address, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RegisterAsync(string address, string endpoint, ulong nonce, byte[] signature, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Docs(Dictionary<string, NodeServices> byEndpoint) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(byEndpoint));

        private sealed class Handler(Dictionary<string, NodeServices> byEndpoint) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(byEndpoint.TryGetValue(request.RequestUri!.GetLeftPart(UriPartial.Authority), out var doc)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(doc, JsonSerializerOptions.Web)) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
