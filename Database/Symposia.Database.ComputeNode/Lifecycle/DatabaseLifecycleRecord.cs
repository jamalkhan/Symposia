namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// The orchestrator's per-database lifecycle row, per the #95 architectural plan's data model.
/// <see cref="StateVersion"/> is the CAS token backing lease-based concurrency control.
/// </summary>
public sealed record DatabaseLifecycleRecord(
    string DatabaseId,
    string Region,
    LifecycleState State,
    long StateVersion,
    DatabaseSize ComputeSize,
    string? PrimaryNodeId,
    IReadOnlyList<string> SafekeeperPeerIds,
    string? BlobBucketId,
    int? IdleSuspendSeconds,
    string? ConnectionString,
    string? FailureReason = null);
