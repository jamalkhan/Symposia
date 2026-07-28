namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>
/// The proxy's outbound call into #95's provisioning/lifecycle service to resume a suspended
/// database's compute node (FR8). Kept as a thin interface here so #93 can be developed/tested
/// independently of #95's implementation.
/// </summary>
public interface ILifecycleClient
{
    Task<ComputeEndpoint> ResumeAsync(string databaseId, CancellationToken cancellationToken);
}
