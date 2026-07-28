namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// A read replica's lifecycle record, per the #96 architectural plan's data model. A replica
/// never owns unique page data -- it points at its parent database's existing Tier 1 bucket via a
/// narrow-scoped, read-only credential, and its "currentness" is entirely a function of
/// <see cref="ReplicaLsn"/> advancing as streamed WAL is applied.
/// </summary>
public sealed record DatabaseReplica(
    string ReplicaId,
    string DatabaseId,
    string Region,
    DatabaseSize ComputeSize,
    ReplicaStatus Status,
    string? NodeId,
    long ReplicaLsn,
    long LagBytes,
    string? ConnectionString);
