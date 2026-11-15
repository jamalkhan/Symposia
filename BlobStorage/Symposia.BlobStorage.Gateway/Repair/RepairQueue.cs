using System.Collections.Concurrent;
using System.Threading.Channels;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;

namespace Symposia.BlobStorage.Gateway.Repair;

public enum RepairReason
{
    CorruptReplica,
    MissingReplica,
    UnderReplicated,

    /// <summary>A holder entered a penalty stage; add copies before data is at risk (#8 FR3).</summary>
    PenaltyPreReplication,

    /// <summary>A soft fault-domain violation or same-region overflow can now be placed better (#8 FR2.4, P2/P3).</summary>
    PlacementImprovement,

    /// <summary>The network has grown; add redundancy beyond the minimum (#8 FR8, P3).</summary>
    OpportunisticRedundancy,
}

/// <summary>Repair tiers and their time-to-completion SLAs (#8 FR4.1): P0 ≤ 5 min, P1 ≤ 30 min, P2 ≤ 2 h, P3 ≤ 24 h.</summary>
public enum RepairPriority
{
    P0 = 0,
    P1 = 1,
    P2 = 2,
    P3 = 3,
}

public sealed record RepairTask(
    string Cid,
    string? BadNodeId,   // null = no specific bad node, just add a copy
    RepairReason Reason)
{
    /// <summary>The tier; defaults by reason (read repair is P0, a newly short object P1).</summary>
    public RepairPriority Priority { get; init; } = Reason switch
    {
        RepairReason.CorruptReplica => RepairPriority.P0,
        RepairReason.MissingReplica or RepairReason.UnderReplicated => RepairPriority.P1,
        RepairReason.PenaltyPreReplication or RepairReason.PlacementImprovement => RepairPriority.P2,
        _ => RepairPriority.P3,
    };

    public DateTimeOffset TriggeredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The task's repair_tasks row (0 when no store is attached).</summary>
    public long AuditId { get; init; }
}

/// <summary>
/// The repair queue, by priority (#8 FR4): one channel per tier, drained P0 first. P0 is unbounded, since
/// dropping a P0 task is the durability incident the tier exists to prevent; P1–P3 are bounded and drop
/// their oldest, which the next scan re-discovers. A CID waits at most once; asking again at a more urgent
/// tier supersedes the waiting task (never the reverse, #8 QA TC-RQ-13/14). Each task is a row in
/// repair_tasks from trigger to completion, so SLA compliance is measurable and survives restarts.
/// </summary>
public sealed class RepairQueue
{
    public static readonly IReadOnlyList<TimeSpan> Slas =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(24)];

    private readonly Channel<RepairTask>[] _tiers;
    private readonly GatewayMetadataStore? _store;

    // CID → the task currently standing for it: sweeps run on every node event, so without this the same
    // objects would be queued over and over (issue #135 review).
    private readonly ConcurrentDictionary<string, RepairTask> _pending = new(StringComparer.OrdinalIgnoreCase);

    public RepairQueue(GatewayMetadataStore? store = null)
    {
        _store = store;
        _tiers =
        [
            Channel.CreateUnbounded<RepairTask>(new UnboundedChannelOptions { SingleReader = true }),
            Bounded(), Bounded(), Bounded(),
        ];
    }

    private Channel<RepairTask> Bounded() => Channel.CreateBounded<RepairTask>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false },
        dropped =>
        {
            // Release the CID so the next scan can queue it again, and close its audit row honestly.
            if (_pending.TryRemove(new KeyValuePair<string, RepairTask>(dropped.Cid, dropped)) && dropped.AuditId > 0)
            {
                _store?.CloseRepairTask(dropped.AuditId, "Dropped");
            }
        });

    /// <summary>Queues the task unless the CID already waits at the same or a more urgent tier; returns whether it was queued.</summary>
    public bool Enqueue(RepairTask task)
    {
        while (true)
        {
            if (_pending.TryGetValue(task.Cid, out var waiting))
            {
                if (waiting.Priority <= task.Priority)
                {
                    return false; // never downgrade or duplicate
                }

                var escalated = Audit(task);
                if (!_pending.TryUpdate(task.Cid, escalated, waiting))
                {
                    continue;
                }

                // The waiting task's row closes as superseded, so no audit row is left open (review: Lenny #8).
                if (waiting.AuditId > 0)
                {
                    _store?.CloseRepairTask(waiting.AuditId, "Superseded");
                }

                _tiers[(int)escalated.Priority].Writer.TryWrite(escalated);
                return true;
            }

            var audited = Audit(task);
            if (!_pending.TryAdd(task.Cid, audited))
            {
                continue;
            }

            if (_tiers[(int)audited.Priority].Writer.TryWrite(audited))
            {
                return true;
            }

            _pending.TryRemove(task.Cid, out _);
            return false;
        }
    }

    private RepairTask Audit(RepairTask task) =>
        _store is null || task.AuditId > 0 ? task
            : task with { AuditId = _store.OpenRepairTask(task.Cid, (int)task.Priority, task.Reason.ToString(), task.BadNodeId, task.TriggeredAt) };

    /// <summary>Re-queues tasks left open by a previous run (the audit rows outlive the in-memory channels).</summary>
    public int Rehydrate()
    {
        if (_store is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var open in _store.OpenRepairTasks(10_000))
        {
            var reason = Enum.TryParse<RepairReason>(open.Reason, out var r) ? r : RepairReason.UnderReplicated;
            if (Enqueue(new RepairTask(open.Cid, open.BadNode, reason)
                {
                    Priority = (RepairPriority)Math.Clamp(open.Priority, 0, 3), TriggeredAt = open.TriggeredAt, AuditId = open.Id,
                }))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The next task, most urgent tier first; superseded tasks are skipped.</summary>
    public async ValueTask<RepairTask> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            foreach (var tier in _tiers)
            {
                while (tier.Reader.TryRead(out var task))
                {
                    if (_pending.TryGetValue(task.Cid, out var current) && ReferenceEquals(current, task))
                    {
                        return task;
                    }
                }
            }

            await Task.WhenAny(_tiers.Select(t => t.Reader.WaitToReadAsync(ct).AsTask()));
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Called by the worker once a task is finished (successfully or not), with what happened.</summary>
    public void Done(RepairTask task, string outcome = "Repaired")
    {
        _pending.TryRemove(new KeyValuePair<string, RepairTask>(task.Cid, task));
        if (task.AuditId > 0)
        {
            _store?.CloseRepairTask(task.AuditId, outcome);
        }
    }

    /// <summary>Releases a CID without an outcome (tests and tooling).</summary>
    public void Done(string cid)
    {
        if (_pending.TryRemove(cid, out var task))
        {
            Done(task);
        }
    }

    /// <summary>Whether a repair for <paramref name="cid"/> is waiting or running (#16 FR7.4).</summary>
    public bool IsPending(string cid) => _pending.ContainsKey(cid);

    /// <summary>How many repairs are waiting.</summary>
    public int Pending => _pending.Count;

    /// <summary>Waiting repairs per tier, for metrics and the SLA endpoint.</summary>
    public IReadOnlyList<int> PendingByTier =>
        [.. Enumerable.Range(0, 4).Select(p => _pending.Values.Count(t => (int)t.Priority == p))];

    /// <summary>
    /// Called from GetObject when a CID mismatch is detected during streaming: read repair, always P0
    /// (#8 FR4.4). Removes the bad node from the object's replica list and enqueues re-replication.
    /// </summary>
    public void EnqueueCorruptReplica(
        ObjectRecord record, string badNodeUrl,
        GatewayMetadataStore store, INodeRegistry nodes)
    {
        RemoveBadNodeFromMetadata(record, badNodeUrl, store);
        Enqueue(new RepairTask(record.Cid, badNodeUrl, RepairReason.CorruptReplica));
    }

    /// <summary>
    /// Called from GetObject when a node returns NotFound for a CID it should have.
    /// Removes the node from the replica list and enqueues re-replication; P0 once that leaves one copy or none.
    /// </summary>
    public void EnqueueMissingReplica(
        ObjectRecord record, string missingNodeUrl,
        GatewayMetadataStore store, INodeRegistry nodes)
    {
        RemoveBadNodeFromMetadata(record, missingNodeUrl, store);
        var left = record.NodeIds.Count(n => !n.Equals(missingNodeUrl, StringComparison.OrdinalIgnoreCase));
        Enqueue(new RepairTask(record.Cid, missingNodeUrl, RepairReason.MissingReplica)
        {
            Priority = left <= 1 ? RepairPriority.P0 : RepairPriority.P1,
        });
    }

    private static void RemoveBadNodeFromMetadata(
        ObjectRecord record, string badNodeUrl, GatewayMetadataStore store)
    {
        // Re-reads inside a transaction and checks the CID, so a stale record (the key was
        // overwritten meanwhile) can't rewrite the newer version's replica list.
        store.RemoveReplica(record.Bucket, record.Key, record.Cid, badNodeUrl);
    }
}
