namespace Symposia.Database.ComputeNode.Lifecycle;

public sealed record ProvisionDatabaseRequest(string DatabaseId, string Region, DatabaseSize ComputeSize, string DatabaseName, int? IdleSuspendSeconds = 900);
