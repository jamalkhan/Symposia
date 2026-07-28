namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// The orchestrator's call into #94's safekeeper coordination for initial assignment (FR3) and
/// re-establishment after a resize-driven primary reassignment.
/// </summary>
public interface ISafekeeperAssignmentClient
{
    Task<IReadOnlyList<string>> AssignSafekeepersAsync(string databaseId, string primaryNodeId, string region, CancellationToken cancellationToken);
}
