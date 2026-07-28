namespace Symposia.Database.ComputeNode.Lifecycle;

public enum LifecycleOperationOutcome
{
    Ok,
    Rejected,
    NotFound,
    NoQualifyingNode,
}

/// <summary>Result of a lifecycle transition attempt (provision/resize/suspend/resume/delete).</summary>
public sealed record LifecycleOperationResult(LifecycleOperationOutcome Outcome, DatabaseLifecycleRecord? Record, string? Reason)
{
    public static LifecycleOperationResult Ok(DatabaseLifecycleRecord record) => new(LifecycleOperationOutcome.Ok, record, null);

    public static LifecycleOperationResult Rejected(string reason, DatabaseLifecycleRecord? record = null) =>
        new(LifecycleOperationOutcome.Rejected, record, reason);

    public static LifecycleOperationResult NotFound(string databaseId) =>
        new(LifecycleOperationOutcome.NotFound, null, $"No database '{databaseId}' exists.");

    public static LifecycleOperationResult NoQualifyingNode() =>
        new(LifecycleOperationOutcome.NoQualifyingNode, null, "No qualifying compute node is available for the requested region/tier.");
}
