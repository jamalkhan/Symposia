using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Gateway handling of read-only and critical storage nodes (issue #199).</summary>
public sealed class ReadOnlyNodeTests
{
    private static NodeConnection Node(string url, bool healthy = true, bool readOnly = false, long available = 1 << 30)
    {
        var node = new NodeConnection(url)
        {
            LastProbe = new ProbeResponse { Healthy = healthy, ReadOnly = readOnly, AvailableStorageBytes = available },
            LastProbeTime = DateTimeOffset.UtcNow,
        };
        return node;
    }

    private sealed class Registry(params NodeConnection[] nodes) : INodeRegistry
    {
        public IReadOnlyList<NodeConnection> All => nodes;

        public IReadOnlyList<NodeConnection> Healthy => nodes.Where(n => n.IsHealthy).ToList();

        public NodeConnection? SelectForRead(IEnumerable<string> nodeUrls) => null;

        public NodeConnection AddNode(string url) => throw new NotSupportedException();

        public bool RemoveNode(string url) => false;
    }

    [Fact]
    public void Writable_SkipsReadOnlyAndTooFullNodes_ButReadOnlyStaysHealthy()
    {
        var a = Node("http://a:1");
        var readOnly = Node("http://b:1", readOnly: true);
        var small = Node("http://c:1", available: 100);
        INodeRegistry nodes = new Registry(a, readOnly, small);

        Assert.True(readOnly.IsHealthy);
        Assert.Equal([a, small], nodes.Writable());
        Assert.Equal([a], nodes.Writable(1000));
    }

    [Fact]
    public void MarkReadOnly_SkipsTheNodeUntilItsNextProbe_WithoutMarkingItUnhealthy()
    {
        var node = Node("http://a:1");

        node.MarkReadOnly();

        Assert.True(node.IsHealthy);
        Assert.False(node.IsWritable);
    }

    [Fact]
    public void CriticalNode_IsUnhealthyButReachable_SoDeletesStillReachIt()
    {
        var critical = Node("http://a:1", healthy: false, readOnly: true);
        Assert.False(critical.IsHealthy);
        Assert.True(critical.IsReachable);

        critical.MarkUnhealthy(); // a failed call: nothing answered
        Assert.False(critical.IsReachable);
    }
}
