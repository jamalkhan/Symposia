namespace Symposia.Network.Node.Telemetry.Committee;

/// <summary>An on-chain checkpoint (committee or self) as the registry stores it.</summary>
public sealed record CommittedCheckpoint(byte[] MerkleRoot, byte[] BlobAddress, uint SampleCount);

/// <summary>A self-checkpoint found via <c>SelfCheckpointCommitted</c> events.</summary>
public sealed record SelfCheckpointRef(ulong Epoch, ushort Checkpoint, string Node, byte[] MerkleRoot, byte[] BlobAddress);

/// <summary>Chain state the committee and the self-checkpoint fallback read (issue #158). Deadlines are always block time.</summary>
public interface ITelemetryChain
{
    /// <summary>Latest block timestamp: the clock every deadline is judged by.</summary>
    Task<long> NowAsync(CancellationToken ct);

    /// <summary>The schedule (genesis, epoch length, windows per epoch) read from the EpochController and registry.</summary>
    Task<TelemetrySchedule> ScheduleAsync(CancellationToken ct);

    Task<(IReadOnlyList<string> Members, int Threshold)> CommitteeAsync(CancellationToken ct);

    Task<long> TelemetryDeadlineAsync(ulong epoch, CancellationToken ct);

    Task<long> SelfCheckpointGraceAsync(CancellationToken ct);

    Task<CommittedCheckpoint?> CheckpointAsync(ulong epoch, ushort checkpoint, CancellationToken ct);

    Task<CommittedCheckpoint?> SelfCheckpointAsync(ulong epoch, ushort checkpoint, string node, CancellationToken ct);

    Task<IReadOnlyList<SelfCheckpointRef>> SelfCheckpointsAsync(ulong epoch, CancellationToken ct);

    Task<int> RecordsPerTxAsync(CancellationToken ct);

    Task<bool> RecordChunkWrittenAsync(ulong epoch, int chunk, CancellationToken ct);

    Task<bool> RecordsFinalizedAsync(ulong epoch, CancellationToken ct);
}

/// <summary>
/// Submits telemetry transactions through the Blockchain Gateway relayer, which pays gas (#158
/// FR5). "Already committed/written" is success; anything else throws.
/// </summary>
public interface ITelemetryRelay
{
    Task CommitCheckpointAsync(ulong epoch, ushort checkpoint, byte[] merkleRoot, byte[] blobAddress, uint sampleCount,
        IReadOnlyList<string> signatures, CancellationToken ct);

    Task SelfCheckpointAsync(string node, ulong epoch, ushort checkpoint, byte[] merkleRoot, byte[] blobAddress, uint sampleCount,
        string signature, byte[] batch, CancellationToken ct);

    Task SubmitRecordsAsync(ulong epoch, int chunkIndex, int chunkCount, IReadOnlyList<AggregatedMetricInput> records,
        IReadOnlyList<string> signatures, CancellationToken ct);
}
