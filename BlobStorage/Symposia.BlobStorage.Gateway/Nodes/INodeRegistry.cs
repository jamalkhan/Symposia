namespace Symposia.BlobStorage.Gateway.Nodes;

public interface INodeRegistry
{
    IReadOnlyList<NodeConnection> All { get; }
    IReadOnlyList<NodeConnection> Healthy { get; }
    NodeConnection? SelectForRead(IEnumerable<string> nodeUrls);
}
