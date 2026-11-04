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
        var now = DateTimeOffset.UtcNow;
        Assert.True(StorageTlsProof.PinMatches(der, now.AddDays(1), pin, now));
        Assert.True(StorageTlsProof.PinMatches(der, now.AddDays(1), pin.ToUpperInvariant(), now));
        Assert.False(StorageTlsProof.PinMatches(RandomNumberGenerator.GetBytes(64), now.AddDays(1), pin, now));
        Assert.False(StorageTlsProof.PinMatches(null, now.AddDays(1), pin, now));
        Assert.False(StorageTlsProof.PinMatches(der, now.AddSeconds(-1), pin, now)); // expired certificate
    }

    [Fact]
    public async Task Source_PinsVouchedCertificates_SkipsBadProofsAndCleartextWhenTlsRequired()
    {
        var good = EthECKey.GenerateKey();
        var forger = EthECKey.GenerateKey();
        var plain = EthECKey.GenerateKey();
        var fp = SHA256.HashData("cert"u8);
        var fpHex = Convert.ToHexStringLower(fp);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        var expired = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        var stale = EthECKey.GenerateKey();
        var registry = new FakeRegistry(
            new RegisteredNode(good.GetPublicAddress(), "http://10.0.0.1:5180"),
            new RegisteredNode(forger.GetPublicAddress(), "http://10.0.0.2:5180"),
            new RegisteredNode(plain.GetPublicAddress(), "http://10.0.0.3:5180"),
            new RegisteredNode(stale.GetPublicAddress(), "http://10.0.0.4:5180"));
        var docs = new Dictionary<string, NodeServices>
        {
            ["http://10.0.0.1:5180"] = new(good.GetPublicAddress(), new("https://10.0.0.1:6180", fpHex, StorageTlsProof.Sign(good, Chain, Registry, fp, notAfter), notAfter)),
            // Proof signed by a different key than the registered one.
            ["http://10.0.0.2:5180"] = new(forger.GetPublicAddress(), new("https://10.0.0.2:6180", fpHex, StorageTlsProof.Sign(good, Chain, Registry, fp, notAfter), notAfter)),
            // A replaced certificate whose proof has expired can't be replayed.
            ["http://10.0.0.4:5180"] = new(stale.GetPublicAddress(), new("https://10.0.0.4:6180", fpHex, StorageTlsProof.Sign(stale, Chain, Registry, fp, expired), expired)),
            ["http://10.0.0.3:5180"] = new(plain.GetPublicAddress(), new("http://10.0.0.3:6180")),
        };

        var required = await new RegistryNodeSource(registry, new Docs(docs), Options(), NullLogger<RegistryNodeSource>.Instance).DiscoverAsync(CancellationToken.None);
        var node = Assert.Single(required);
        Assert.Equal((good.GetPublicAddress(), fpHex), (node.Id, node.TlsFingerprint));

        var optional = await new RegistryNodeSource(registry, new Docs(docs), Options(requireTls: false), NullLogger<RegistryNodeSource>.Instance).DiscoverAsync(CancellationToken.None);
        Assert.Equal(2, optional.Count);
    }

    [Fact]
    public void Sync_RefusesCleartextWhenTlsIsRequired_FromAnySource()
    {
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        using var nodes = new NodeRegistry(Options(requireTls: true), store, NullLogger<NodeRegistry>.Instance);
        nodes.Sync([new StorageNodeDescriptor("http://static:6180", "http://static:6180")]);
        Assert.Empty(nodes.All);
    }

    [Fact]
    public void Sync_NeverDowngradesANodeOnceSeenWithTls()
    {
        var store = new GatewayMetadataStore(Options(requireTls: false));
        store.Initialize();
        using var nodes = new NodeRegistry(Options(requireTls: false), store, NullLogger<NodeRegistry>.Instance);
        nodes.Sync([new StorageNodeDescriptor("0xA", "https://10.0.0.1:6180", "aa")]);

        nodes.Sync([new StorageNodeDescriptor("0xA", "http://10.0.0.1:6180")]); // e.g. a rewritten services document

        var node = Assert.Single(nodes.All);
        Assert.Equal("aa", node.TlsFingerprint);
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
