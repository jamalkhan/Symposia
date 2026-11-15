using System.Collections.Concurrent;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Placement;
using Symposia.BlobStorage.Gateway.Repair;

namespace Symposia.BlobStorage.Gateway.Penalties;

/// <summary>A node moved to a penalty stage 0–4 (Requirements/Network/node-runner-incentives-and-penalties.md).</summary>
public sealed record NodePenaltyStageChanged(string NodeId, int Stage, DateTimeOffset At);

/// <summary>
/// Where penalty-stage transitions come from (#8 FR3.1). The penalty system itself is separate and not built
/// yet; this narrow contract is what blob redundancy consumes, so the reactions are built and tested now and
/// the real source swaps in by DI.
/// </summary>
public interface INodePenaltyEventSource
{
    event Action<NodePenaltyStageChanged>? StageChanged;

    int StageOf(string nodeId);
}

/// <summary>Stages set by an operator through <c>POST /admin/nodes/penalty</c>, standing in for the on-chain system.</summary>
public sealed class ManualPenaltySource : INodePenaltyEventSource
{
    private readonly ConcurrentDictionary<string, int> _stages = new(StringComparer.OrdinalIgnoreCase);

    public event Action<NodePenaltyStageChanged>? StageChanged;

    public int StageOf(string nodeId) => _stages.GetValueOrDefault(nodeId);

    public void Set(string nodeId, int stage)
    {
        stage = Math.Clamp(stage, 0, 4);
        if (_stages.TryGetValue(nodeId, out var old) && old == stage)
        {
            return;
        }

        _stages[nodeId] = stage;
        StageChanged?.Invoke(new NodePenaltyStageChanged(nodeId, stage, DateTimeOffset.UtcNow));
    }
}

/// <summary>
/// Integrity failures this gateway observed (#8 FR6.4/6.5), sent out to the penalty system. The outbound
/// half of the contract; until that system exists the default records and logs them.
/// </summary>
public interface IIntegrityFailureSink
{
    void Report(string nodeId, string cid, string detail);
}

public sealed class LoggingIntegrityFailureSink(ILogger<LoggingIntegrityFailureSink> logger) : IIntegrityFailureSink
{
    public void Report(string nodeId, string cid, string detail) =>
        logger.LogWarning("Integrity failure on {Node} for {Cid}: {Detail}", nodeId, cid, detail);
}

/// <summary>
/// The penalty stage a node's challenge record argues for (#8 AC9): what this gateway would propose to the
/// penalty system. Advisory: only the penalty system moves a node between stages.
/// </summary>
public static class PenaltyAssessment
{
    public static int RecommendedStage(int challengeFailures) => challengeFailures switch
    {
        >= 10 => 3,
        >= 6 => 2,
        >= 3 => 1,
        _ => 0,
    };
}

/// <summary>
/// Applies penalty stages to the gateway's view of nodes and reacts within one cycle (#8 FR3.1, AC4):
/// <list type="bullet">
/// <item>Stage 1: one extra copy of every blob on the node (target + 1), at P2.</item>
/// <item>Stage 2: two extra copies, at P1; reads prefer healthy replicas.</item>
/// <item>Stage 3: offline for placement and reads; its blobs are migrated elsewhere, at P1.</item>
/// <item>Stage 4: every blob it held is audited; any below its minimum without it goes to P0.</item>
/// </list>
/// Extra copies are recorded as pending replicas (#8 FR3.2) and drawn from the shared bandwidth budget.
/// </summary>
public sealed class PenaltyResponder(
    INodePenaltyEventSource source, INodeRegistry nodes, GatewayMetadataStore store, RepairQueue queue,
    PlacementEngine placement, ILogger<PenaltyResponder> logger) : IHostedService
{
    private const int Page = 500;

    public Task StartAsync(CancellationToken ct)
    {
        source.StageChanged += OnStageChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        source.StageChanged -= OnStageChanged;
        return Task.CompletedTask;
    }

    private void OnStageChanged(NodePenaltyStageChanged change)
    {
        try
        {
            Apply(change);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reacting to {Node} entering penalty stage {Stage} failed.", change.NodeId, change.Stage);
        }
    }

    /// <summary>Applies one transition; public for tests and for sources that deliver synchronously.</summary>
    public int Apply(NodePenaltyStageChanged change)
    {
        var node = nodes.All.FirstOrDefault(n => n.Id.Equals(change.NodeId, StringComparison.OrdinalIgnoreCase));
        if (node is not null)
        {
            node.PenaltyStage = change.Stage;
        }

        logger.LogWarning("Storage node {Node} is in penalty stage {Stage}.", change.NodeId, change.Stage);
        var affected = 0;
        (string Bucket, string Key)? cursor = null;
        while (true)
        {
            var page = store.ObjectsOnNode(change.NodeId, Page, cursor);
            foreach (var record in page)
            {
                React(record, change);
                affected++;
            }

            if (page.Count < Page) break;
            cursor = (page[^1].Bucket, page[^1].Key);
        }

        return affected;
    }

    private void React(ObjectRecord record, NodePenaltyStageChanged change)
    {
        var baseline = placement.Requirement(record.Regions, record.SizeBytes).RepairTarget;
        switch (change.Stage)
        {
            case 1 or 2:
                var extra = change.Stage;
                store.RaiseTargetCopies(record.Bucket, record.Key, record.Cid, baseline + extra);
                store.AddPendingReplica(record.Cid, change.NodeId, change.Stage, extra);
                queue.Enqueue(new RepairTask(record.Cid, null, RepairReason.PenaltyPreReplication)
                {
                    Priority = change.Stage == 1 ? RepairPriority.P2 : RepairPriority.P1,
                });
                break;
            case 3:
                // Migrate: copy elsewhere, then drop this node's copy (repair evicts the bad node once it has).
                queue.Enqueue(new RepairTask(record.Cid, change.NodeId, RepairReason.PenaltyPreReplication) { Priority = RepairPriority.P1 });
                break;
            case 4:
                var others = record.NodeIds.Count(n => !n.Equals(change.NodeId, StringComparison.OrdinalIgnoreCase));
                store.RemoveReplica(record.Bucket, record.Key, record.Cid, change.NodeId);
                queue.Enqueue(new RepairTask(record.Cid, change.NodeId, RepairReason.MissingReplica)
                {
                    Priority = others < Math.Max(1, record.TargetCopies) ? RepairPriority.P0 : RepairPriority.P1,
                });
                break;
        }
    }
}
