using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Repair;

namespace Symposia.BlobStorage.Gateway.GC;

public enum GcVerdict
{
    Clear,

    /// <summary>Not now (a repair is copying from it): retry later.</summary>
    Deferred,

    /// <summary>Never: deleting would break an invariant. The task is dropped and an operator alerted.</summary>
    Refused,
}

/// <summary>
/// The one check every GC deletion passes before any bytes go (#16 FR7, AC10). Categories differ in what
/// makes a blob garbage; none of them may skip these:
/// <list type="number">
/// <item>FR7.1: nothing readable through the API references the blob (a live object, a shard of one, an
/// in-progress multipart part, or a version still in its recovery window).</item>
/// <item>FR7.2: no object or retained version holding it is locked or on legal hold.</item>
/// <item>FR7.3: a replica isn't evicted for placement reasons while its object is short of its target.</item>
/// <item>FR7.4: no repair is in flight for it; GC waits until replication is stable.</item>
/// </list>
/// </summary>
public sealed class SafetyInvariantGate(GatewayMetadataStore store, RepairQueue repairs, ILogger<SafetyInvariantGate> logger)
{
    public long Refusals { get; private set; }

    public GcVerdict Check(string cid, string nodeId, string category)
    {
        if (repairs.IsPending(cid))
        {
            return GcVerdict.Deferred; // FR7.4
        }

        if (store.IsCidHeld(cid))
        {
            return Refuse(cid, nodeId, category, "locked or on legal hold"); // FR7.2
        }

        if (category == "StaleReplicaPurge" && store.GetObjectByCid(cid) is { } live)
        {
            // FR7.3: evicting this copy is only safe once the object holds its target without it.
            return live.NodeIds.Count(n => !n.Equals(nodeId, StringComparison.OrdinalIgnoreCase)) >= live.TargetCopies
                ? GcVerdict.Clear
                : GcVerdict.Deferred;
        }

        if (store.IsCidReferenced(cid))
        {
            // FR7.1. A plain deletion of a blob something references again (dedup) is routine; any other
            // category reaching here means an evaluator queued live data: a bug worth an alert.
            return category is "BlobDelete" or "SoftDeletePurge" ? Drop(cid) : Refuse(cid, nodeId, category, "still readable");
        }

        return GcVerdict.Clear;
    }

    private static GcVerdict Drop(string cid) => GcVerdict.Refused;

    private GcVerdict Refuse(string cid, string nodeId, string category, string why)
    {
        Refusals++;
        logger.LogError("GC refused to delete {Cid} from {Node} ({Category}): {Why}. An evaluator queued protected data.", cid, nodeId, category, why);
        return GcVerdict.Refused;
    }
}

/// <summary>
/// Whether the chain has no metadata record for a blob (#16 FR5.3). Until the coordination layer exposes
/// this, the default answers "can't confirm" for everything: orphans are detected and aged, never deleted.
/// </summary>
public interface IOnChainMetadataOracle
{
    Task<bool> ConfirmsNoRecordAsync(string cid, CancellationToken ct);
}

public sealed class FailClosedOracle : IOnChainMetadataOracle
{
    public Task<bool> ConfirmsNoRecordAsync(string cid, CancellationToken ct) => Task.FromResult(false);
}
