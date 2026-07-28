using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Symposia.Database.ComputeNode.Archival;

/// <summary>
/// Per-node, per-timeline async archiver sidecar (issue #94 Arch): polls each local timeline's
/// quorum-confirmed WAL and uploads segments in LSN order to that tenant's Tier 1 bucket, advancing
/// a durable per-timeline <c>archived_lsn</c> watermark only after a successful upload. Local WAL
/// garbage collection is gated on <c>archived_lsn</c>, not <c>commit_lsn</c> -- this is what gives
/// "retry without dropping" and "no GC before archival" for free, as a consequence of the watermark
/// design rather than separately-tracked failure-handling logic.
///
/// Segments are processed strictly in LSN order per timeline: a failed upload is retried in place
/// rather than skipped, so the archived stream can never gap even under sustained failures.
/// </summary>
public sealed class WalArchiver
{
    private readonly IBlobUploader _uploader;
    private readonly ComputeNodeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private readonly Dictionary<string, string> _tenantByTimeline = [];
    private readonly Dictionary<string, Queue<WalSegment>> _pending = [];
    private readonly Dictionary<string, long> _archivedLsn = [];
    private readonly Dictionary<string, int> _consecutiveFailures = [];
    private readonly HashSet<string> _escalated = [];

    public WalArchiver(IBlobUploader uploader, IOptions<ComputeNodeOptions> options, ILogger<WalArchiver> logger, TimeProvider? timeProvider = null)
    {
        _uploader = uploader;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Queues a quorum-confirmed segment for archival. Callers must enqueue in ascending LSN order per timeline.</summary>
    public void Enqueue(string tenantDatabaseId, WalSegment segment)
    {
        lock (_gate)
        {
            _tenantByTimeline[segment.TimelineId] = tenantDatabaseId;
            if (!_pending.TryGetValue(segment.TimelineId, out var queue))
                _pending[segment.TimelineId] = queue = new Queue<WalSegment>();
            queue.Enqueue(segment);
        }
    }

    /// <summary>
    /// Attempts to upload the head-of-queue segment for <paramref name="timelineId"/>. On success,
    /// advances <c>archived_lsn</c> and dequeues; on transient failure, leaves the segment queued
    /// for retry; on permanent failure, also leaves it queued (never dropped) but flags escalation
    /// for operator visibility distinct from ordinary transient retry.
    /// </summary>
    public async Task<UploadOutcome?> ProcessNextAsync(string timelineId, CancellationToken cancellationToken = default)
    {
        WalSegment segment;
        string tenantDatabaseId;
        lock (_gate)
        {
            if (!_pending.TryGetValue(timelineId, out var queue) || queue.Count == 0)
                return null;
            segment = queue.Peek();
            tenantDatabaseId = _tenantByTimeline[timelineId];
        }

        var outcome = await _uploader.UploadAsync(tenantDatabaseId, segment, cancellationToken);

        lock (_gate)
        {
            var queue = _pending[timelineId];
            switch (outcome)
            {
                case UploadOutcome.Success:
                    queue.Dequeue();
                    _archivedLsn[timelineId] = segment.Lsn;
                    _consecutiveFailures[timelineId] = 0;
                    _escalated.Remove(timelineId);
                    break;

                case UploadOutcome.TransientFailure:
                    _consecutiveFailures[timelineId] = _consecutiveFailures.GetValueOrDefault(timelineId) + 1;
                    _logger.LogWarning(
                        "Transient archival failure for timeline {Timeline} segment {Sequence}; will retry ({Attempts} consecutive failures).",
                        timelineId, segment.SequenceNumber, _consecutiveFailures[timelineId]);
                    break;

                case UploadOutcome.PermanentFailure:
                    _consecutiveFailures[timelineId] = _consecutiveFailures.GetValueOrDefault(timelineId) + 1;
                    if (_escalated.Add(timelineId))
                    {
                        _logger.LogError(
                            "Permanent archival failure for timeline {Timeline} segment {Sequence}; escalating for operator visibility. Segment is retained, not dropped.",
                            timelineId, segment.SequenceNumber);
                    }
                    break;
            }
        }

        return outcome;
    }

    /// <summary>Bounded exponential backoff before the next retry attempt for a timeline, capped so transient failures never hammer blob storage.</summary>
    public TimeSpan NextRetryDelay(string timelineId)
    {
        lock (_gate)
        {
            var failures = _consecutiveFailures.GetValueOrDefault(timelineId);
            if (failures == 0)
                return TimeSpan.Zero;

            var seconds = _options.ArchivalRetryBackoffBaseSeconds * Math.Pow(2, failures - 1);
            return TimeSpan.FromSeconds(Math.Min(seconds, _options.ArchivalMaxRetryBackoffSeconds));
        }
    }

    public bool IsEscalated(string timelineId)
    {
        lock (_gate)
        {
            return _escalated.Contains(timelineId);
        }
    }

    public long GetArchivedLsn(string timelineId)
    {
        lock (_gate)
        {
            return _archivedLsn.GetValueOrDefault(timelineId);
        }
    }

    /// <summary>A segment is eligible for local GC only once its LSN is at or below the confirmed-archived watermark (FR8).</summary>
    public bool IsEligibleForGc(string timelineId, long segmentLsn)
    {
        lock (_gate)
        {
            return segmentLsn <= _archivedLsn.GetValueOrDefault(timelineId);
        }
    }

    /// <summary>Sum of not-yet-archived segment bytes queued for a timeline -- the archival backlog metric (FR11).</summary>
    public long GetBacklogBytes(string timelineId)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(timelineId, out var queue) ? queue.Sum(s => (long)s.SizeBytes) : 0;
        }
    }
}
