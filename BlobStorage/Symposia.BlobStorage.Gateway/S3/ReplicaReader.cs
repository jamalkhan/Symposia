using Grpc.Core;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.S3;

/// <summary>A replica stream that has started: the node, the call, and whether a first chunk arrived.</summary>
public sealed record OpenedReplica(NodeConnection Node, AsyncServerStreamingCall<ReadBlobChunk> Call, bool HasFirst);

/// <summary>
/// Opens a blob for reading from the first replica that starts streaming (#135), shared by GET and
/// multipart Complete (#5 addendum). Healthy nodes are tried first; a node that errors or sends nothing
/// for <see cref="FirstChunkTimeout"/> is marked unhealthy and the next is tried. Nothing reaches the
/// caller until a first chunk arrives, so a just-failed node costs a retry, not a failed request.
/// </summary>
public static class ReplicaReader
{
    public static readonly TimeSpan FirstChunkTimeout = TimeSpan.FromSeconds(10);

    public static IReadOnlyList<NodeConnection> Order(INodeRegistry nodes, IEnumerable<string> nodeIds)
    {
        var ids = nodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return nodes.All.Where(n => ids.Contains(n.Id))
            .OrderByDescending(n => n.IsHealthy)
            .ThenByDescending(n => n.LastProbeTime)
            .ToList();
    }

    /// <param name="onNotFound">Called when a replica says it doesn't have the blob (callers queue a repair).</param>
    /// <param name="precheck">Optional per-replica check before reading (e.g. an integrity challenge); false skips the replica.</param>
    public static async Task<OpenedReplica?> OpenAsync(
        IReadOnlyList<NodeConnection> replicas, ReadBlobRequest request, ILogger logger, CancellationToken ct,
        Action<NodeConnection>? onNotFound = null, Func<NodeConnection, Task<bool>>? precheck = null)
    {
        foreach (var node in replicas)
        {
            if (precheck is not null && !await precheck(node))
            {
                continue;
            }

            var call = node.Client.ReadBlob(request, deadline: DateTime.UtcNow.AddHours(1), cancellationToken: ct);
            using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
            first.CancelAfter(FirstChunkTimeout);
            try
            {
                var hasFirst = await call.ResponseStream.MoveNext(first.Token);
                return new OpenedReplica(node, call, hasFirst);
            }
            catch (RpcException ex) when (!ct.IsCancellationRequested)
            {
                call.Dispose();
                if (ex.StatusCode == StatusCode.NotFound)
                {
                    logger.LogWarning("Node {Url} returned NotFound for CID {Cid}; trying another replica.", node.Url, request.Cid);
                    onNotFound?.Invoke(node);
                }
                else
                {
                    logger.LogWarning("Read of CID {Cid} from {Url} failed ({Status}); trying another replica.", request.Cid, node.Url, ex.StatusCode);
                    node.MarkUnhealthy();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                call.Dispose();
                logger.LogWarning("Read of CID {Cid} from {Url} sent nothing for {Timeout}; trying another replica.", request.Cid, node.Url, FirstChunkTimeout);
                node.MarkUnhealthy();
            }
        }

        return null;
    }
}
