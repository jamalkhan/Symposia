namespace Symposia.Database.ComputeNode.Databases;

public enum TenantDatabaseState
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Unhealthy,
}

/// <summary>A tenant Postgres database placed on this node by orchestration (#95's local control API client).</summary>
public sealed record TenantDatabase(
    string TenantDatabaseId,
    int PostgresMajorVersion,
    string[] Extensions,
    string[] SafekeeperPeers,
    TenantDatabaseState State);
