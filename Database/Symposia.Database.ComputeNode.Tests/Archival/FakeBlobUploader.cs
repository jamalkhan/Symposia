using Symposia.Database.ComputeNode.Archival;

namespace Symposia.Database.ComputeNode.Tests.Archival;

public sealed class FakeBlobUploader : IBlobUploader
{
    private readonly Queue<UploadOutcome> _scriptedOutcomes;

    public List<WalSegment> UploadAttempts { get; } = [];

    public FakeBlobUploader(params UploadOutcome[] scriptedOutcomes)
    {
        _scriptedOutcomes = new Queue<UploadOutcome>(scriptedOutcomes);
    }

    public Task<UploadOutcome> UploadAsync(string tenantDatabaseId, WalSegment segment, CancellationToken cancellationToken)
    {
        UploadAttempts.Add(segment);
        var outcome = _scriptedOutcomes.Count > 0 ? _scriptedOutcomes.Dequeue() : UploadOutcome.Success;
        return Task.FromResult(outcome);
    }
}
