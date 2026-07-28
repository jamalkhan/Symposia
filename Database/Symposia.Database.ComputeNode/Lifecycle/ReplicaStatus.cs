namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>Read replica status per the #96 architectural plan's data model.</summary>
public enum ReplicaStatus
{
    Provisioning,
    Healthy,
    Lagging,
    Suspended,
    Deleting,
    Deleted,
    Promoted,
    Failed,
}
