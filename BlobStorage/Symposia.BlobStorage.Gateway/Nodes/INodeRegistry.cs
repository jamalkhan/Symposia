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

    /// <summary>
    /// Every known node has been probed at least once, so health data reflects reality rather than the
    /// startup assumption (#1 FR-24 <c>node_cache_not_loaded</c>). Registries without probing are always loaded.
    /// </summary>
    bool CacheLoaded => true;
    NodeConnection AddNode(string url);
    bool RemoveNode(string url);
}
