namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>Result of a replica lifecycle transition attempt (provision/resize/suspend/resume/delete/promote).</summary>
public sealed record ReplicaOperationResult(LifecycleOperationOutcome Outcome, DatabaseReplica? Replica, string? Reason)
{
    public static ReplicaOperationResult Ok(DatabaseReplica replica) => new(LifecycleOperationOutcome.Ok, replica, null);

    public static ReplicaOperationResult Rejected(string reason, DatabaseReplica? replica = null) =>
        new(LifecycleOperationOutcome.Rejected, replica, reason);

    public static ReplicaOperationResult NotFound(string replicaId) =>
        new(LifecycleOperationOutcome.NotFound, null, $"No replica '{replicaId}' exists.");

    public static ReplicaOperationResult NoQualifyingNode() =>
        new(LifecycleOperationOutcome.NoQualifyingNode, null, "No qualifying compute node is available for the requested region/tier.");
}
