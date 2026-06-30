// StorageNode has a Program class in the global namespace, same as Gateway — use an assembly alias
// so we can refer to both without ambiguity (see .csproj Aliases attributes).
extern alias NodeAlias;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// End-to-end integration tests that boot a real StorageNode and a real Gateway in-process and
/// exercise the S3-compatible API using the official AWSSDK.S3 client.
///
/// Architecture:
///   AmazonS3Client → Gateway (TestServer) → NodeConnection (in-memory gRPC) → StorageNode (TestServer)
///
/// The Gateway's NodeRegistry is replaced by TestNodeRegistry, which holds a single NodeConnection
/// backed by the StorageNode's TestServer HttpClient. No TCP ports are used.
/// </summary>
[Collection("GatewayIntegration")]
public sealed class GatewayIntegrationTests : IClassFixture<GatewayFixture>
{
    private const string Bucket = "test-bucket";
    private readonly GatewayFixture _f;

    public GatewayIntegrationTests(GatewayFixture f) => _f = f;

    // ── Bucket operations ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBucket_NewBucket_ReturnsSuccess()
    {
        var resp = await _f.S3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = "new-bucket-" + Guid.NewGuid().ToString("N")[..8],
            UseClientRegion = true,
        });
        Assert.Equal(HttpStatusCode.OK, resp.HttpStatusCode);
    }

    [Fact]
    public async Task HeadBucket_ExistingBucket_ReturnsOk()
    {
        // Bucket is pre-created in fixture.
        var resp = await _f.S3.GetBucketLocationAsync(new GetBucketLocationRequest
        {
            BucketName = Bucket,
        });
        Assert.Equal(HttpStatusCode.OK, resp.HttpStatusCode);
    }

    // ── PutObject ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutObject_SmallPayload_Returns200WithETag()
    {
        var key = Key();
        var body = "hello, symposia"u8.ToArray();

        var resp = await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key,
            InputStream = new MemoryStream(body),
            ContentType = "text/plain",
        });

        Assert.Equal(HttpStatusCode.OK, resp.HttpStatusCode);
        Assert.NotEmpty(resp.ETag);
    }

    [Fact]
    public async Task PutObject_LargePayload_FansOutSuccessfully()
    {
        // 256 KB — larger than the 64 KB fan-out chunk in QuorumWriter.
        var key = Key();
        var body = new byte[256 * 1024];
        Random.Shared.NextBytes(body);

        var resp = await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key,
            InputStream = new MemoryStream(body),
            ContentType = "application/octet-stream",
        });

        Assert.Equal(HttpStatusCode.OK, resp.HttpStatusCode);
    }

    // ── GetObject ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutObject_ThenGetObject_ReturnsIdenticalBytes()
    {
        var key = Key();
        var body = Encoding.UTF8.GetBytes("round-trip payload");

        await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key,
            InputStream = new MemoryStream(body), ContentType = "text/plain",
            DisablePayloadSigning = true,
        });

        var get = await _f.S3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = Bucket, Key = key,
        });

        using var ms = new MemoryStream();
        await get.ResponseStream.CopyToAsync(ms);
        Assert.Equal(body, ms.ToArray());
        Assert.Equal("text/plain", get.Headers.ContentType);
    }

    [Fact]
    public async Task GetObject_WithByteRange_ReturnsSlice()
    {
        var key = Key();
        var body = "0123456789abcdef"u8.ToArray();
        await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key,
            InputStream = new MemoryStream(body), ContentType = "application/octet-stream",
            DisablePayloadSigning = true,
        });

        var get = await _f.S3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = Bucket, Key = key,
            ByteRange = new ByteRange(4, 9),
        });

        using var ms = new MemoryStream();
        await get.ResponseStream.CopyToAsync(ms);
        Assert.Equal("456789"u8.ToArray(), ms.ToArray());
        Assert.Equal(HttpStatusCode.PartialContent, get.HttpStatusCode);
    }

    [Fact]
    public async Task GetObject_NonExistentKey_Returns404()
    {
        // AWSSDK v4 throws the specific subclass NoSuchKeyException, not the base AmazonS3Exception.
        var ex = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            _f.S3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = Bucket, Key = "does-not-exist/" + Guid.NewGuid(),
            }));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    // ── HeadObject ────────────────────────────────────────────────────────────

    [Fact]
    public async Task HeadObject_ExistingKey_ReturnsMetadata()
    {
        var key = Key();
        var body = "head test"u8.ToArray();
        await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key,
            InputStream = new MemoryStream(body), ContentType = "text/plain",
            DisablePayloadSigning = true,
        });

        var meta = await _f.S3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = Bucket, Key = key,
        });

        Assert.Equal(HttpStatusCode.OK, meta.HttpStatusCode);
        Assert.Equal(body.Length, meta.ContentLength);
    }

    [Fact]
    public async Task HeadObject_NonExistentKey_Returns404()
    {
        var ex = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            _f.S3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = Bucket, Key = "missing/" + Guid.NewGuid(),
            }));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    // ── ListObjects ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListObjects_AfterPutObject_ObjectAppearsInListing()
    {
        var key = "list-test/" + Key();
        await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key,
            InputStream = new MemoryStream("content"u8.ToArray()),
            ContentType = "text/plain",
            DisablePayloadSigning = true,
        });

        var list = await _f.S3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket, Prefix = "list-test/",
        });

        Assert.Contains(list.S3Objects, o => o.Key == key);
    }

    [Fact]
    public async Task ListObjects_PrefixFilter_ReturnsOnlyMatchingKeys()
    {
        var prefix = "prefix-filter-" + Guid.NewGuid().ToString("N")[..8] + "/";
        var matchKey = prefix + "match.txt";
        var noMatchKey = "other-" + Guid.NewGuid().ToString("N")[..8] + "/no-match.txt";

        await Task.WhenAll(
            _f.S3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket, Key = matchKey,
                InputStream = new MemoryStream("a"u8.ToArray()), ContentType = "text/plain",
                DisablePayloadSigning = true,
            }),
            _f.S3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket, Key = noMatchKey,
                InputStream = new MemoryStream("b"u8.ToArray()), ContentType = "text/plain",
                DisablePayloadSigning = true,
            }));

        var list = await _f.S3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket, Prefix = prefix,
        });

        Assert.All(list.S3Objects, o => Assert.StartsWith(prefix, o.Key));
        Assert.Contains(list.S3Objects, o => o.Key == matchKey);
    }

    // ── DeleteObject ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteObject_ExistingKey_RemovesFromListing()
    {
        var prefix = "delete-test-" + Guid.NewGuid().ToString("N")[..8] + "/";
        var key = prefix + "to-delete.txt";
        await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key,
            InputStream = new MemoryStream("bye"u8.ToArray()), ContentType = "text/plain",
            DisablePayloadSigning = true,
        });

        await _f.S3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = key });

        var list = await _f.S3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket, Prefix = prefix,
        });
        // S3Objects is null when the bucket is empty after deletion.
        Assert.True(list.S3Objects is null || !list.S3Objects.Any(o => o.Key == key));
    }

    [Fact]
    public async Task DeleteObject_NonExistentKey_Returns204()
    {
        // S3 spec: DeleteObject is always idempotent.
        var resp = await _f.S3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = Bucket, Key = "never-existed/" + Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.HttpStatusCode);
    }

    // ── CopyObject ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyObject_CreatesSecondMetadataEntry_BothKeysResolvable()
    {
        var srcKey = "copy-src/" + Key();
        var dstKey = "copy-dst/" + Key();
        var body = "copy content"u8.ToArray();

        await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = srcKey,
            InputStream = new MemoryStream(body), ContentType = "text/plain",
            DisablePayloadSigning = true,
        });

        await _f.S3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Bucket, SourceKey = srcKey,
            DestinationBucket = Bucket, DestinationKey = dstKey,
        });

        var srcGet = await _f.S3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = srcKey });
        var dstGet = await _f.S3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = dstKey });

        var srcBytes = await ReadAllAsync(srcGet.ResponseStream);
        var dstBytes = await ReadAllAsync(dstGet.ResponseStream);

        Assert.Equal(body, srcBytes);
        Assert.Equal(body, dstBytes);
        // Content-addressed: both keys share the same ETag (CID).
        Assert.Equal(srcGet.ETag, dstGet.ETag);
    }

    // ── Content-addressed dedup ───────────────────────────────────────────────

    [Fact]
    public async Task PutObject_SameContentTwice_BothETagsIdentical()
    {
        var body = "dedup-content"u8.ToArray();
        var key1 = "dedup/" + Key();
        var key2 = "dedup/" + Key();

        var r1 = await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key1,
            InputStream = new MemoryStream(body), ContentType = "application/octet-stream",
            DisablePayloadSigning = true,
        });
        var r2 = await _f.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key2,
            InputStream = new MemoryStream(body), ContentType = "application/octet-stream",
            DisablePayloadSigning = true,
        });

        Assert.Equal(r1.ETag, r2.ETag);
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnauthenticatedRequest_Returns403()
    {
        var resp = await _f.GatewayHttpClient.GetAsync($"/{Bucket}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Request_WrongSecretKey_Returns403()
    {
        var badS3 = _f.BuildS3Client(secretKey: "wrong-secret-key");
        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            badS3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket }));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    // ── Health endpoints ──────────────────────────────────────────────────────

    [Fact]
    public async Task HealthLive_Returns200()
    {
        var resp = await _f.GatewayHttpClient.GetAsync("/healthz/live");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task HealthReady_Returns200()
    {
        var resp = await _f.GatewayHttpClient.GetAsync("/healthz/ready");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Key() => Guid.NewGuid().ToString("N")[..16];

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }
}

// ── Fixture ───────────────────────────────────────────────────────────────────

/// <summary>
/// Boots an in-process StorageNode and Gateway once per test class run.
/// The Gateway's NodeRegistry is replaced with <see cref="TestNodeRegistry"/> which routes
/// gRPC calls through the StorageNode's in-memory TestServer — no TCP sockets needed.
/// </summary>
public sealed class GatewayFixture : IAsyncLifetime
{
    private NodeFactory? _nodeFactory;
    private GatewayFactory? _gatewayFactory;

    public AmazonS3Client S3 { get; private set; } = null!;
    public HttpClient GatewayHttpClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _nodeFactory = new NodeFactory();
        var nodeHttpClient = _nodeFactory.CreateClient();

        _gatewayFactory = new GatewayFactory(nodeHttpClient);
        GatewayHttpClient = _gatewayFactory.CreateClient();

        S3 = BuildS3Client();

        // Pre-create the shared test bucket.
        await S3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = "test-bucket",
            UseClientRegion = true,
        });
    }

    public AmazonS3Client BuildS3Client(
        string accessKey = "AKIAIOSFODNN7EXAMPLE",
        string secretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")
    {
        // AWSSDK v4 builds URIs with DangerousDisablePathAndQueryCanonicalization, which
        // ASP.NET Core's TestHost.ClientHandler cannot parse. Wrap with a normalizing handler
        // that reconstructs a standard Uri before forwarding to the in-memory test server.
        var testHandler = _gatewayFactory!.Server.CreateHandler();
        var normalizingHandler = new UriNormalizingHandler(testHandler);
        var httpClient = new HttpClient(normalizingHandler, disposeHandler: true)
        {
            BaseAddress = _gatewayFactory.Server.BaseAddress,
        };

        return new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                ServiceURL = httpClient.BaseAddress!.ToString().TrimEnd('/'),
                ForcePathStyle = true,
                HttpClientFactory = new FixedHttpClientFactory(httpClient),
                DisableLogging = true,
                Timeout = TimeSpan.FromSeconds(30),
            });
    }

    // Fixes two AWSSDK v3/TestServer incompatibilities:
    // 1. AWSSDK builds URIs with DangerousDisablePathAndQueryCanonicalization;
    //    ASP.NET Core TestHost can't parse them — reconstruct as a standard Uri.
    // 2. AWSSDK validates the ETag from GET/HEAD responses as MD5, but our ETag is
    //    SHA-256 (the content CID). Strip ETag from read responses so the SDK has
    //    nothing to compare against, without affecting PutObject ETag assertions.
    private sealed class UriNormalizingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
                request.RequestUri = new Uri(request.RequestUri.AbsoluteUri);
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
                response.Headers.Remove("ETag");
            return response;
        }
    }

    public async Task DisposeAsync()
    {
        S3.Dispose();
        GatewayHttpClient.Dispose();
        _gatewayFactory?.Dispose();
        _nodeFactory?.Dispose();
        await Task.CompletedTask;
    }

    // Passes a fixed HttpClient to the AWS SDK so requests go through the TestServer.
    private sealed class FixedHttpClientFactory : Amazon.Runtime.HttpClientFactory
    {
        private readonly HttpClient _client;
        public FixedHttpClientFactory(HttpClient client) => _client = client;
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => _client;
    }
}

// ── Factories ─────────────────────────────────────────────────────────────────

/// <summary>Boots the StorageNode in a temp directory, disposed after tests.</summary>
internal sealed class NodeFactory
    : WebApplicationFactory<NodeAlias::Symposia.BlobStorage.StorageNode.Program>
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"symposia-gw-node-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageNode:StorageRoot"] = Path.Combine(_dataDir, "blobs"),
                ["StorageNode:ManifestDbPath"] = Path.Combine(_dataDir, "manifest.db"),
                ["StorageNode:NodeIdentityKeyPath"] = Path.Combine(_dataDir, "node-identity.pem"),
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }
}

/// <summary>
/// Boots the Gateway and replaces NodeRegistry with a single in-process node
/// backed by the StorageNode TestServer's HttpClient.
/// </summary>
internal sealed class GatewayFactory
    : WebApplicationFactory<Program>
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"symposia-gw-meta-{Guid.NewGuid():N}");
    private readonly HttpClient _nodeHttpClient;

    public GatewayFactory(HttpClient nodeHttpClient) => _nodeHttpClient = nodeHttpClient;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:MetadataDbPath"] = Path.Combine(_dataDir, "gateway-metadata.db"),
                ["Gateway:WriteQuorumCount"] = "1",
                ["Gateway:Region"] = "us-east-1",
                // Credentials the S3 client will sign with.
                ["Gateway:Credentials:0:AccessKeyId"] = "AKIAIOSFODNN7EXAMPLE",
                ["Gateway:Credentials:0:SecretAccessKey"] = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
                ["Gateway:Credentials:0:TenantId"] = "test-tenant",
            }));

        builder.ConfigureTestServices(services =>
        {
            // Keep NodeRegistry registered — its IHostedService factory delegate still resolves it
            // and it will just probe an empty node list harmlessly. Only replace INodeRegistry so
            // QuorumWriter and read paths use our in-process TestNodeRegistry.
            services.RemoveAll<INodeRegistry>();

            var fakeNode = new NodeConnection("http://in-process-node", _nodeHttpClient);
            services.AddSingleton<INodeRegistry>(new TestNodeRegistry(fakeNode));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>Single-node registry for integration tests — always reports the one node as healthy.</summary>
internal sealed class TestNodeRegistry : INodeRegistry
{
    private readonly NodeConnection _node;
    public TestNodeRegistry(NodeConnection node) => _node = node;

    public IReadOnlyList<NodeConnection> All => [_node];
    public IReadOnlyList<NodeConnection> Healthy => [_node];

    public NodeConnection? SelectForRead(IEnumerable<string> nodeUrls) => _node;
}
