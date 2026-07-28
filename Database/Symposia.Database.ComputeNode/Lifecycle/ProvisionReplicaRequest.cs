namespace Symposia.Database.ComputeNode.Lifecycle;

public sealed record ProvisionReplicaRequest(string DatabaseId, string ReplicaId, DatabaseSize ComputeSize, string DatabaseName, string? Region = null);
