namespace Symposia.BlobStorage.Gateway.Repair;

/// <summary>What the compliance scan observed about one blob's copies.</summary>
/// <param name="Target">Copies the blob must have.</param>
/// <param name="Online">Copies on healthy nodes that passed their challenge.</param>
/// <param name="Counted">Copies that still count: online, plus offline ones within the 15-minute grace period.</param>
/// <param name="CountedOnStage2Plus">Of the counted copies, how many sit on nodes in penalty stage 2 or higher.</param>
/// <param name="OnStage1">Copies on stage-1 nodes.</param>
/// <param name="SoftViolationFixable">A recorded soft fault-domain gap now has a compliant alternative.</param>
/// <param name="OverflowFixable">An overflow copy shares a region and a fresh region now has nodes.</param>
/// <param name="CanGrow">The network has room to add redundancy beyond the minimum (P3 growth).</param>
public sealed record ReplicaObservation(
    int Target, int Online, int Counted, int CountedOnStage2Plus, int OnStage1,
    bool SoftViolationFixable = false, bool OverflowFixable = false, bool CanGrow = false);

/// <summary>
/// Maps an observation to exactly one repair tier, per the trigger table in
/// Requirements/BlobStorage/redundancy-and-data-integrity.md (#8 FR4.1, AC5). Pure, so every trigger is
/// table-testable. The grace period never delays P0 (#8 FR4.2): one copy or none left is P0 however recently
/// the others went offline.
/// </summary>
public static class RepairClassifier
{
    public static (RepairPriority Priority, RepairReason Reason)? Classify(ReplicaObservation o)
    {
        // P0: 1 or 0 healthy copies left, grace period or not.
        if (o.Online <= 1 && o.Target > 1)
        {
            return (RepairPriority.P0, RepairReason.UnderReplicated);
        }

        // P0: below minimum and every remaining copy is on a stage 2+ node.
        if (o.Counted < o.Target && o.Counted > 0 && o.CountedOnStage2Plus == o.Counted)
        {
            return (RepairPriority.P0, RepairReason.UnderReplicated);
        }

        // P1: below minimum (a node went offline past its grace, or a copy was lost).
        if (o.Counted < o.Target)
        {
            return (RepairPriority.P1, RepairReason.UnderReplicated);
        }

        // Within the grace period: offline copies still count, and nothing is created (#8 AC6).
        // P2: at minimum but a copy sits on a stage-1 node.
        if (o.OnStage1 > 0 && o.Counted <= o.Target)
        {
            return (RepairPriority.P2, RepairReason.PenaltyPreReplication);
        }

        // P2: a soft fault-domain violation can now be placed better.
        if (o.SoftViolationFixable)
        {
            return (RepairPriority.P2, RepairReason.PlacementImprovement);
        }

        // P3: an overflow copy can move to a new region; or the network has grown and redundancy can too.
        if (o.OverflowFixable)
        {
            return (RepairPriority.P3, RepairReason.PlacementImprovement);
        }

        if (o.CanGrow)
        {
            return (RepairPriority.P3, RepairReason.OpportunisticRedundancy);
        }

        return null;
    }
}
