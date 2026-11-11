namespace Symposia.BlobStorage.Gateway.Nodes;

public interface INodeRegistry
{
    IReadOnlyList<NodeConnection> All { get; }
    IReadOnlyList<NodeConnection> Healthy { get; }

    /// <summary>
    /// Healthy nodes that take new writes (#199): not read-only, and with room for
    /// <paramref name="sizeBytes"/> when it is known. Read-only nodes still serve reads and repairs.
    /// </summary>
    IReadOnlyList<NodeConnection> Writable(long? sizeBytes = null) =>
        Healthy.Where(n => n.IsWritable && n.CanFit(sizeBytes)).ToList();
    NodeConnection? SelectForRead(IEnumerable<string> nodeUrls);
    NodeConnection AddNode(string url);
    bool RemoveNode(string url);
}
