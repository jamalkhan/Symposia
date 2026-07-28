namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>
/// Wake-on-connect orchestration (FR8, AC7, WAKE-04): when a connection arrives for a suspended
/// database, triggers resume via #95's lifecycle service and holds the caller until the compute
/// node is ready, rather than failing the connection outright. Concurrent connection attempts for
/// the same database dedupe to a single in-flight resume call -- per the #93 arch, a thundering
/// herd of redundant resume calls against the lifecycle service is exactly what shard-sticky
/// routing plus this coordinator's dedupe are meant to prevent.
/// </summary>
public sealed class WakeOnConnectCoordinator(ILifecycleClient lifecycleClient, RoutingTableService routingTable)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<ComputeEndpoint>> _inFlightResumes = [];

    public async Task<ComputeEndpoint> WakeAsync(string databaseId, CancellationToken cancellationToken = default)
    {
        Task<ComputeEndpoint> resumeTask;
        lock (_gate)
        {
            if (!_inFlightResumes.TryGetValue(databaseId, out resumeTask!))
            {
                resumeTask = ResumeAndUpdateRoutingAsync(databaseId, cancellationToken);
                _inFlightResumes[databaseId] = resumeTask;
            }
        }

        try
        {
            return await resumeTask;
        }
        finally
        {
            lock (_gate)
            {
                if (_inFlightResumes.TryGetValue(databaseId, out var current) && current == resumeTask)
                    _inFlightResumes.Remove(databaseId);
            }
        }
    }

    private async Task<ComputeEndpoint> ResumeAndUpdateRoutingAsync(string databaseId, CancellationToken cancellationToken)
    {
        var endpoint = await lifecycleClient.ResumeAsync(databaseId, cancellationToken);
        routingTable.UpdatePrimary(databaseId, endpoint);
        return endpoint;
    }

    public int InFlightResumeCount(string databaseId)
    {
        lock (_gate)
        {
            return _inFlightResumes.ContainsKey(databaseId) ? 1 : 0;
        }
    }
}
