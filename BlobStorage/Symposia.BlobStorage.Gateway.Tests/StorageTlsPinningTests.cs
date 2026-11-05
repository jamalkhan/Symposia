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

    private IOptions<GatewayOptions> Options() => Microsoft.Extensions.Options.Options.Create(new GatewayOptions
    {
        MetadataDbPath = Path.Combine(_dir, "meta.db"),
        ProbeIntervalSeconds = 3600,
        ChainId = Chain,
        NodeRegistryAddress = Registry,
    });

    /// <summary>A policy with <c>storage.grpc.tls</c> activated on chain (#145), or with nothing activated.</summary>
    private NetworkPolicy Policy(bool tlsActive)
    {
        var policy = new NetworkPolicy(Options(), NullLogger<NetworkPolicy>.Instance);
        if (tlsActive)
        {
            policy.Apply(new ProtocolSchedule(true, 0, new ProtocolSchedule.MinVersion(0, 0, 0),
                [new ProtocolSchedule.Activation(ProtocolFeatures.StorageGrpcTls, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds())]));
        }

        return policy;
    }

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
    public async Task Source_PinsVouchedCertificates_SkipsBadProofs_AndCleartextOnceTlsIsActivated()
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

        var required = await new RegistryNodeSource(registry, new Docs(docs), Policy(tlsActive: true), Options(), NullLogger<RegistryNodeSource>.Instance).DiscoverAsync(CancellationToken.None);
        var node = Assert.Single(required, d => d.RefusedBecause is null);
        Assert.Equal((good.GetPublicAddress(), fpHex), (node.Id, node.TlsFingerprint));
        Assert.Equal(3, required.Count(d => d.RefusedBecause is not null)); // reported, so the registry drops them at once

        // Before activation the cleartext-only node is still used; bad proofs are refused either way.
        var optional = await new RegistryNodeSource(registry, new Docs(docs), Policy(tlsActive: false), Options(), NullLogger<RegistryNodeSource>.Instance).DiscoverAsync(CancellationToken.None);
        Assert.Equal(2, optional.Count(d => d.RefusedBecause is null));
    }

    [Fact]
    public void Sync_RefusesCleartextOnceTlsIsActivated_FromAnySource()
    {
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        using var nodes = new NodeRegistry(Options(), store, NullLogger<NodeRegistry>.Instance, Policy(tlsActive: true));
        nodes.Sync([new StorageNodeDescriptor("http://static:6180", "http://static:6180")]);
        Assert.Empty(nodes.All);
    }

    // #145 AC4: a node the policy refuses stops being used on that sync, not after the grace period.
    [Fact]
    public void Sync_DropsARefusedNodeImmediately()
    {
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        using var nodes = new NodeRegistry(Options(), store, NullLogger<NodeRegistry>.Instance, Policy(tlsActive: false));
        nodes.Sync([new StorageNodeDescriptor("0xA", "https://10.0.0.1:6280", "aa")]);

        IReadOnlyList<NodeConnection>? dropped = null;
        nodes.NodesDropped += d => dropped = d; // the gateway wires this to an immediate repair sweep
        nodes.Sync([StorageNodeDescriptor.Refused("0xA", "it lacks required protocol feature(s) storage.x")]);

        Assert.Empty(nodes.All);
        Assert.Equal("0xA", Assert.Single(dropped!).Id);
    }

    [Fact]
    public void Sync_NeverDowngradesANodeOnceSeenWithTls()
    {
        var store = new GatewayMetadataStore(Options());
        store.Initialize();
        using var nodes = new NodeRegistry(Options(), store, NullLogger<NodeRegistry>.Instance, Policy(tlsActive: false));
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
        using var nodes = new NodeRegistry(Options(), store, NullLogger<NodeRegistry>.Instance, new NetworkPolicy(Options(), NullLogger<NetworkPolicy>.Instance));
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
