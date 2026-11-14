extern alias NodeAlias;

using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// A gateway over several real in-process storage nodes (#1, #7, #8 tests). Each node's gRPC traffic can
/// pass through a <see cref="DelegatingHandler"/>, so a test can cut a stream mid-way or black-hole a node
/// without touching node code. Nodes start healthy (a successful probe recorded), with optional regions
/// and addresses for placement tests.
/// </summary>
internal sealed class TestCluster : IDisposable
{
    public const string Bucket = "cluster-bucket";

    private readonly List<NodeFactory> _nodeFactories = [];

    private TestCluster(int nodes, Func<int, DelegatingHandler?>? handler, Func<int, ProbeResponse>? probe,
        IReadOnlyDictionary<string, string?>? config, Action<IServiceCollection>? services)
    {
        for (var i = 0; i < nodes; i++)
        {
            var factory = new NodeFactory();
            _nodeFactories.Add(factory);
            var server = factory.Server;
            HttpMessageHandler inner = server.CreateHandler();
            if (handler?.Invoke(i) is { } wrap)
            {
                wrap.InnerHandler = inner;
                inner = wrap;
            }

            var connection = new NodeConnection($"http://node-{i}", new HttpClient(inner) { BaseAddress = server.BaseAddress })
            {
                LastProbe = probe?.Invoke(i) ?? new ProbeResponse { Healthy = true, AvailableStorageBytes = 1L << 30 },
                LastProbeTime = DateTimeOffset.UtcNow,
                LastProbeAttemptAt = DateTimeOffset.UtcNow,
            };
            Connections.Add(connection);
        }

        Registry = new ClusterRegistry(Connections);
        Gateway = new GatewayFactory(_ => Registry, config, services);
        Http = Gateway.CreateClient();
        S3 = GatewayFixture.S3ClientFor(Gateway.Server);
    }

    public List<NodeConnection> Connections { get; } = [];

    public ClusterRegistry Registry { get; }

    public GatewayFactory Gateway { get; }

    public HttpClient Http { get; }

    /// <summary>A SigV4-signing client for raw requests, as the default test tenant.</summary>
    public HttpClient Signed(string accessKey = "AKIAIOSFODNN7EXAMPLE", string secretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY") =>
        new(new SigV4SigningHandler(Gateway.Server.CreateHandler(), accessKey, secretKey)) { BaseAddress = Gateway.Server.BaseAddress };

    public AmazonS3Client S3 { get; }

    public IServiceProvider Services => Gateway.Services;

    public static async Task<TestCluster> StartAsync(int nodes, IReadOnlyDictionary<string, string?>? config = null,
        Func<int, DelegatingHandler?>? handler = null, Func<int, ProbeResponse>? probe = null, Action<IServiceCollection>? services = null)
    {
        var cluster = new TestCluster(nodes, handler, probe, config, services);
        await cluster.S3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket, UseClientRegion = true });
        return cluster;
    }

    public async Task<string> PutAsync(string key, byte[] body)
    {
        var response = await S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = key, InputStream = new MemoryStream(body), UseChunkEncoding = false,
        });
        return response.ETag.Trim('"');
    }

    public async Task<byte[]> GetAsync(string key)
    {
        using var response = await S3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = key });
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        S3.Dispose();
        Http.Dispose();
        Gateway.Dispose();
        foreach (var node in _nodeFactories)
        {
            node.Dispose();
        }
    }

    /// <summary>The cluster's nodes as a registry: healthy as probed, nothing added or removed.</summary>
    internal sealed class ClusterRegistry(IReadOnlyList<NodeConnection> nodes) : INodeRegistry
    {
        public IReadOnlyList<NodeConnection> All => nodes;

        public IReadOnlyList<NodeConnection> Healthy => nodes.Where(n => n.IsHealthy).ToList();

        public NodeConnection? SelectForRead(IEnumerable<string> nodeUrls) =>
            nodes.Where(n => nodeUrls.Contains(n.Id, StringComparer.OrdinalIgnoreCase)).OrderByDescending(n => n.IsHealthy).FirstOrDefault();

        public NodeConnection AddNode(string url) => throw new NotSupportedException();

        public bool RemoveNode(string url) => false;
    }
}

/// <summary>
/// Cuts a node's ReadBlob responses after <paramref name="bytes"/> bytes, as a node dying mid-stream would.
/// Handlers sharing a <paramref name="cuts"/> budget cut that many reads in total, whichever node serves them.
/// </summary>
internal sealed class CutReadsHandler(long bytes, System.Runtime.CompilerServices.StrongBox<int>? cuts = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (request.RequestUri?.AbsolutePath.EndsWith("/ReadBlob", StringComparison.Ordinal) == true
            && (cuts is null || Interlocked.Decrement(ref cuts.Value) >= 0))
        {
            var inner = await response.Content.ReadAsStreamAsync(ct);
            var cut = new StreamContent(new CutStream(inner, bytes));
            foreach (var header in response.Content.Headers)
            {
                cut.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = cut;
        }

        return response;
    }

    private sealed class CutStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_read >= limit)
            {
                throw new IOException("connection reset by test");
            }

            var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - _read)], ct);
            _read += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Fails every gRPC call to a node with a connection error, as an unreachable node would.</summary>
internal sealed class BlackHoleHandler : DelegatingHandler
{
    public bool Enabled { get; set; } = true;

    public Func<HttpRequestMessage, bool> Matches { get; set; } = _ => true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Enabled && Matches(request) ? throw new HttpRequestException("node unreachable (test)") : base.SendAsync(request, ct);
}
