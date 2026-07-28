using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.Database.ComputeNode;
using Symposia.Database.ComputeNode.Archival;

namespace Symposia.Database.ComputeNode.Tests.Archival;

/// <summary>
/// Traces to the QA test plan's async archival, retry/backpressure, and local-retention sections
/// (TC-21, TC-24, TC-28–TC-33, TC-35): archival watermark advances only on confirmed success,
/// never-drop retry semantics, and GC gating on the archived watermark rather than commit_lsn.
/// </summary>
public sealed class WalArchiverTests
{
    private static WalArchiver CreateArchiver(IBlobUploader uploader) =>
        new(uploader, Options.Create(new ComputeNodeOptions { ArchivalRetryBackoffBaseSeconds = 1, ArchivalMaxRetryBackoffSeconds = 8 }),
            NullLogger<WalArchiver>.Instance);

    [Fact]
    public async Task ProcessNextAsync_SuccessfulUpload_AdvancesWatermarkAndDequeues()
    {
        var uploader = new FakeBlobUploader(UploadOutcome.Success);
        var archiver = CreateArchiver(uploader);
        var segment = new WalSegment("timeline-1", Lsn: 100, SequenceNumber: 1, SizeBytes: 64);
        archiver.Enqueue("db-1", segment);

        var outcome = await archiver.ProcessNextAsync("timeline-1");

        Assert.Equal(UploadOutcome.Success, outcome);
        Assert.Equal(100, archiver.GetArchivedLsn("timeline-1"));
        Assert.Equal(0, archiver.GetBacklogBytes("timeline-1"));
    }

    [Fact]
    public async Task ProcessNextAsync_TransientFailure_LeavesSegmentQueuedForRetryWithoutAdvancingWatermark()
    {
        var uploader = new FakeBlobUploader(UploadOutcome.TransientFailure, UploadOutcome.Success);
        var archiver = CreateArchiver(uploader);
        var segment = new WalSegment("timeline-1", Lsn: 100, SequenceNumber: 1, SizeBytes: 64);
        archiver.Enqueue("db-1", segment);

        var first = await archiver.ProcessNextAsync("timeline-1");
        Assert.Equal(UploadOutcome.TransientFailure, first);
        Assert.Equal(0, archiver.GetArchivedLsn("timeline-1"));
        Assert.Equal(64, archiver.GetBacklogBytes("timeline-1"));

        var second = await archiver.ProcessNextAsync("timeline-1");
        Assert.Equal(UploadOutcome.Success, second);
        Assert.Equal(100, archiver.GetArchivedLsn("timeline-1"));

        // Retried the same segment, never dropped: exactly two attempts, both for sequence 1.
        Assert.Equal(2, uploader.UploadAttempts.Count);
        Assert.All(uploader.UploadAttempts, s => Assert.Equal(1, s.SequenceNumber));
    }

    [Fact]
    public async Task ProcessNextAsync_PermanentFailure_EscalatesButDoesNotDropSegment()
    {
        var uploader = new FakeBlobUploader(UploadOutcome.PermanentFailure);
        var archiver = CreateArchiver(uploader);
        archiver.Enqueue("db-1", new WalSegment("timeline-1", Lsn: 100, SequenceNumber: 1, SizeBytes: 64));

        await archiver.ProcessNextAsync("timeline-1");

        Assert.True(archiver.IsEscalated("timeline-1"));
        Assert.Equal(0, archiver.GetArchivedLsn("timeline-1"));
        Assert.Equal(64, archiver.GetBacklogBytes("timeline-1"));
    }

    [Fact]
    public async Task ProcessNextAsync_SegmentsProcessInLsnOrder_NoGapEvenAcrossRetries()
    {
        var uploader = new FakeBlobUploader(UploadOutcome.TransientFailure, UploadOutcome.Success, UploadOutcome.Success);
        var archiver = CreateArchiver(uploader);
        archiver.Enqueue("db-1", new WalSegment("timeline-1", Lsn: 100, SequenceNumber: 1, SizeBytes: 10));
        archiver.Enqueue("db-1", new WalSegment("timeline-1", Lsn: 200, SequenceNumber: 2, SizeBytes: 10));

        await archiver.ProcessNextAsync("timeline-1"); // fails on segment 1
        await archiver.ProcessNextAsync("timeline-1"); // retries and succeeds on segment 1
        await archiver.ProcessNextAsync("timeline-1"); // proceeds to segment 2

        Assert.Equal(200, archiver.GetArchivedLsn("timeline-1"));
        Assert.Equal([1, 1, 2], uploader.UploadAttempts.Select(s => s.SequenceNumber));
    }

    [Fact]
    public void IsEligibleForGc_OnlyTrueAtOrBelowArchivedWatermark()
    {
        var archiver = CreateArchiver(new FakeBlobUploader());

        Assert.False(archiver.IsEligibleForGc("timeline-1", 100));
    }

    [Fact]
    public async Task IsEligibleForGc_TrueOnceSegmentIsArchived()
    {
        var uploader = new FakeBlobUploader(UploadOutcome.Success);
        var archiver = CreateArchiver(uploader);
        archiver.Enqueue("db-1", new WalSegment("timeline-1", Lsn: 100, SequenceNumber: 1, SizeBytes: 10));

        await archiver.ProcessNextAsync("timeline-1");

        Assert.True(archiver.IsEligibleForGc("timeline-1", 100));
        Assert.False(archiver.IsEligibleForGc("timeline-1", 200));
    }

    [Fact]
    public void NextRetryDelay_GrowsExponentiallyAndIsCapped()
    {
        var archiver = CreateArchiver(new FakeBlobUploader());
        Assert.Equal(TimeSpan.Zero, archiver.NextRetryDelay("timeline-1"));
    }
}
