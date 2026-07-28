namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// Database lifecycle states per the #95 spec/Arch. <see cref="Resuming"/> and <see cref="Failed"/>
/// are the Arch plan's internal refinements of the six tenant-documented states (provisioning,
/// active, suspended, resizing, deleting, deleted) -- a resume has observable duration, and a
/// stuck operation must be visible rather than leaving the tenant staring at an indefinite state.
/// </summary>
public enum LifecycleState
{
    Provisioning,
    Active,
    Resizing,
    Suspended,
    Resuming,
    Deleting,
    Deleted,
    Failed,
}
