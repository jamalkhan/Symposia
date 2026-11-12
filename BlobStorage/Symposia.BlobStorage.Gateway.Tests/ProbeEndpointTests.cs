using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.Signer;
using Symposia.BlobStorage.Gateway.History;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Rewards;
using Symposia.Network.Node;
using Symposia.Network.Node.Rewards;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// What the Verifier measures a gateway through (#225 FR3), against a real in-process storage node: the canary read
/// path, the challenge answer, the signer's reserved lane, and the signed epoch figure.
/// </summary>
public sealed class ProbeEndpointTests : IAsyncLifetime
{
    private const string Distributor = "0x5FbDB2315678afecb367f032d93F642f64180aa3";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym225-" + Guid.NewGuid().ToString("N"));
    private readonly EthECKey _signer = EthECKey.GenerateKey();
    private NodeFactory _node = null!;
    private ProbeGatewayFactory _gateway = null!;
    private NodeIdentity _identity = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _identity = new NodeIdentity(Microsoft.Extensions.Options.Options.Create(new NodeNetworkOptions { NodeIdentityKeyPath = Path.Combine(_dir, "node.key") }));
        _identity.EnsureLoadedOrGenerated();
        _node = new NodeFactory();
        _gateway = new ProbeGatewayFactory(_node.CreateClient(), _dir, _identity, _signer.GetPublicAddress());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _gateway.DisposeAsync();
        await _node.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private HttpRequestMessage Probe(EthECKey key, byte[] nonce, long? ts = null)
    {
        var at = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = new EthereumMessageSigner().Sign(ProbeChallenge.Digest(_identity.NodeId, at, nonce), key);
        var request = new HttpRequestMessage(HttpMethod.Get, ProbeChallenge.Path);
        request.Headers.Add(ProbeChallenge.Header, ProbeChallenge.Format(at, nonce, signature));
        return request;
    }

    private Task<bool> WriteCanaryAsync() => new CanaryService(_gateway.Services.GetRequiredService<IServiceScopeFactory>(),
        _gateway.Services.GetRequiredService<GatewayMetadataStore>(), _identity, NullLogger<CanaryService>.Instance).EnsureAsync(default);

    [Fact]
    public async Task NoCanary_Is503()
    {
        var response = await _gateway.CreateClient().SendAsync(Probe(_signer, ProbeChallenge.NewNonce()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Canary_IsReadThroughStorage_AndAnswersTheChallenge()
    {
        Assert.True(await WriteCanaryAsync());
        var nonce = ProbeChallenge.NewNonce();

        var response = await _gateway.CreateClient().SendAsync(Probe(_signer, nonce));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProbeChallenge.Response>();
        Assert.Equal(ProbeChallenge.Answer(nonce, ProbeChallenge.Canary(_identity.NodeId)), body!.Answer);
    }

    [Fact]
    public async Task NoChallenge_Is400()
    {
        var response = await _gateway.CreateClient().GetAsync(ProbeChallenge.Path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Strangers_AreRateLimited_ButTheMetricSignerKeepsItsLane()
    {
        Assert.True(await WriteCanaryAsync());
        var client = _gateway.CreateClient();
        var stranger = EthECKey.GenerateKey();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            statuses.Add((await client.SendAsync(Probe(stranger, ProbeChallenge.NewNonce()))).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Probe(_signer, ProbeChallenge.NewNonce()))).StatusCode);
    }

    [Fact]
    public async Task StaleChallenge_DoesNotGetTheSignerLane()
    {
        Assert.True(await WriteCanaryAsync());
        var client = _gateway.CreateClient();
        var old = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        for (var i = 0; i < 6; i++)
        {
            await client.SendAsync(Probe(EthECKey.GenerateKey(), ProbeChallenge.NewNonce())); // use up this IP's shared lane
        }

        var response = await client.SendAsync(Probe(_signer, ProbeChallenge.NewNonce(), old));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Canary_IsInvisibleToTenants_AndKeptOutOfHistory()
    {
        Assert.True(await WriteCanaryAsync());
        await _gateway.CreateClient().SendAsync(Probe(_signer, ProbeChallenge.NewNonce()));

        Assert.False(_gateway.Services.GetRequiredService<GatewayMetadataStore>().BucketExists(ProbeChallenge.CanaryBucket));
        Assert.False(BucketNames.IsValid(ProbeChallenge.CanaryBucket));
        _gateway.Services.GetRequiredService<HistoryRecorder>().FlushNow();
        Assert.Empty(_gateway.Services.GetRequiredService<HistoryStore>().Query(ProbeChallenge.CanaryBucket, ProbeChallenge.CanaryKey(_identity.NodeId), null, null, null, 0, 10));
    }

    [Fact]
    public async Task EpochReport_IsSignedByTheNodeKey_OnceMeasured()
    {
        var client = _gateway.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(EpochReportSignature.PathPrefix + "7")).StatusCode);

        _gateway.Services.GetRequiredService<HistoryStore>().SaveMeasurement(
            new EpochMeasurement(Distributor, 7, 0, 1, new EpochFactors(10_000, 10_000, 3, 123_456, 9), 1));
        var report = await client.GetFromJsonAsync<EpochReportSignature.Report>(EpochReportSignature.PathPrefix + "7");

        Assert.Equal(123_456, report!.BytesServed);
        Assert.True(EpochReportSignature.IsValid(report, 31337, Distributor, _identity.NodeId, 7));
        Assert.False(EpochReportSignature.IsValid(report with { BytesServed = 999_999_999 }, 31337, Distributor, _identity.NodeId, 7));
    }

    private sealed class FixedSignerChain(string signer) : IServiceMetricsChain
    {
        public Task<ulong> ChainIdAsync(CancellationToken ct) => Task.FromResult(31337UL);
        public Task<ulong> CurrentEpochAsync(CancellationToken ct) => Task.FromResult(0UL);
        public Task<long> EpochStartAsync(ulong epoch, CancellationToken ct) => Task.FromResult(0L);
        public Task<string> MetricSignerAsync(CancellationToken ct) => Task.FromResult(signer);
    }

    private sealed class ProbeGatewayFactory(HttpClient nodeHttpClient, string dir, NodeIdentity identity, string signer) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:MetadataDbPath"] = Path.Combine(dir, "gateway-metadata.db"),
                ["Gateway:WriteQuorumCount"] = "1",
                ["Gateway:MinCopiesPerObject"] = "1",
                ["Gateway:ServiceMetrics:RewardDistributorAddress"] = Distributor,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<INodeRegistry>();
                services.AddSingleton<INodeRegistry>(new TestNodeRegistry(new NodeConnection("http://in-process-node", nodeHttpClient)));
                services.AddSingleton(identity);
                services.AddSingleton<IServiceMetricsChain>(new FixedSignerChain(signer));
            });
        }
    }
}
