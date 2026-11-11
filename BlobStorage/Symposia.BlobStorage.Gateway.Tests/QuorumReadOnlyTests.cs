extern alias NodeAlias;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Quorum;
using Symposia.BlobStorage.Protocol;
using NodeStorage = NodeAlias::Symposia.BlobStorage.StorageNode.Storage;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// QuorumWriter against two in-process storage nodes, one of them read-only for disk space but
/// still advertised as writable by a stale probe (issue #199): the write lands on the other node,
/// the refusing node is skipped for writes from then on, and it is never marked unhealthy.
/// </summary>
public sealed class QuorumReadOnlyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"symposia-q199-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(16, 300L << 20, 10L << 30)] // read-only (free below unused allocation): refused at the header
    [InlineData(1 << 20, 500L << 30, 512L << 10)] // writable but 512 KiB allocated: refused while the body streams
    public async Task RefusingNode_IsDroppedFromTheWrite_AndSkippedAfter_WithoutBeingMarkedUnhealthy(int size, long free, long allocated)
    {
        using var writable = new DiskNodeFactory(Path.Combine(_dir, "a"), free: 500L << 30);
        using var readOnly = new DiskNodeFactory(Path.Combine(_dir, "b"), free, allocated);
        var a = Connection("http://a", writable);
        var b = Connection("http://b", readOnly);
        var registry = new Registry(a, b);
        var writer = Writer(registry);

        var result = await writer.WriteBlobAsync("t", "bucket", "key", new UnseekableStream(new byte[size]), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(["http://a"], result!.NodeIds);
        Assert.True(b.IsHealthy);
        Assert.False(b.IsWritable);
        Assert.Equal([a], ((INodeRegistry)registry).Writable());
    }

    [Fact]
    public async Task EveryNodeReadOnly_MissesQuorum()
    {
        using var readOnly = new DiskNodeFactory(Path.Combine(_dir, "b"), free: 300L << 20);
        var b = Connection("http://b", readOnly);
        var writer = Writer(new Registry(b));

        Assert.Null(await writer.WriteBlobAsync("t", "bucket", "key", new MemoryStream(new byte[16]), CancellationToken.None));
        Assert.True(b.IsHealthy);
    }

    private QuorumWriter Writer(INodeRegistry registry)
    {
        var options = Options.Create(new GatewayOptions { MetadataDbPath = Path.Combine(_dir, "gateway.db"), WriteQuorumCount = 1, MinCopiesPerObject = 2 });
        var store = new GatewayMetadataStore(options);
        store.Initialize();
        return new QuorumWriter(registry, store, options, new Repair.RepairQueue());
    }

    // Probed as healthy and writable: the gateway's view lags the node's.
    private static NodeConnection Connection(string url, DiskNodeFactory factory) => new(url, factory.CreateClient())
    {
        LastProbe = new ProbeResponse { Healthy = true, AvailableStorageBytes = 1L << 30 },
        LastProbeTime = DateTimeOffset.UtcNow,
    };

    private sealed class Registry(params NodeConnection[] nodes) : INodeRegistry
    {
        public IReadOnlyList<NodeConnection> All => nodes;

        public IReadOnlyList<NodeConnection> Healthy => nodes.Where(n => n.IsHealthy).ToList();

        public NodeConnection? SelectForRead(IEnumerable<string> nodeUrls) => null;

        public NodeConnection AddNode(string url) => throw new NotSupportedException();

        public bool RemoveNode(string url) => false;
    }

    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class FixedDisk(long free) : NodeStorage.IDiskSpace
    {
        public (long Free, long Total) Measure(string path) => (free, 1000L << 30);
    }

    private sealed class DiskNodeFactory(string dataDir, long free, long allocated = 10L << 30)
        : WebApplicationFactory<NodeAlias::Symposia.BlobStorage.StorageNode.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageNode:StorageRoot"] = Path.Combine(dataDir, "blobs"),
                ["StorageNode:ManifestDbPath"] = Path.Combine(dataDir, "manifest.db"),
                ["StorageNode:NodeIdentityKeyPath"] = Path.Combine(dataDir, "node-identity.pem"),
                ["StorageNode:MaxCapacityBytes"] = allocated.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<NodeStorage.IDiskSpace>(new FixedDisk(free)));
        }
    }
}
