using System.Collections.Concurrent;
using Nethereum.Hex.HexConvertors.Extensions;

namespace Symposia.Network.Node.Telemetry.Committee;

/// <summary>
/// A committee member's EIP-712 <c>TelemetryCheckpoint</c> signature over one window's batch
/// (issue #158). The proposer's own endorsement doubles as the proposal: the signature
/// authenticates it, and other members endorse only after validating the batch themselves. The
/// signature is exactly what <c>commitCheckpoint</c> verifies on-chain.
/// </summary>
public sealed record CheckpointEndorsement(
    ulong Epoch, ushort Checkpoint, string MerkleRoot, string BlobAddress, uint SampleCount, string Member, string Signature)
{
    public (ulong, ushort, string, string, uint) Content =>
        (Epoch, Checkpoint, MerkleRoot.ToLowerInvariant(), BlobAddress.ToLowerInvariant(), SampleCount);

    public bool IsValid(TelemetryEip712 eip712) =>
        string.Equals(TelemetryEip712.Recover(eip712.CheckpointDigest(Epoch, Checkpoint, MerkleRoot.HexToByteArray(), BlobAddress.HexToByteArray(), SampleCount), Signature),
            Member, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A member's EIP-712 <c>AggregatedRecords</c> signature over one records chunk.</summary>
public sealed record RecordsSignature(ulong Epoch, int ChunkIndex, int ChunkCount, string Digest, string Member, string Signature)
{
    public bool IsValid() =>
        string.Equals(TelemetryEip712.Recover(Digest.HexToByteArray(), Signature), Member, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Everything one gossip request carries.</summary>
public sealed record TelemetryEnvelope(
    IReadOnlyList<SignedTelemetry<HeartbeatSample>>? Heartbeats = null,
    IReadOnlyList<SignedTelemetry<Attestation>>? Attestations = null,
    IReadOnlyList<CheckpointEndorsement>? Endorsements = null,
    IReadOnlyList<RecordsSignature>? RecordSignatures = null)
{
    public int Count => (Heartbeats?.Count ?? 0) + (Attestations?.Count ?? 0) + (Endorsements?.Count ?? 0) + (RecordSignatures?.Count ?? 0);
}

/// <summary>
/// Committee signatures seen so far, keyed by window or records chunk and de-duplicated per
/// member and content. Holds only what has a valid signature from its claimed member; whether
/// that member is on the committee is checked by the consumer against chain state.
/// </summary>
public sealed class CommitteeMessageStore
{
    private readonly ConcurrentDictionary<(ulong, ushort), ConcurrentDictionary<(string, string), CheckpointEndorsement>> _endorsements = new();
    private readonly ConcurrentDictionary<(ulong, int), ConcurrentDictionary<(string, string), RecordsSignature>> _records = new();

    /// <summary>Adds the endorsement; false if it was already held.</summary>
    public bool Add(CheckpointEndorsement e) =>
        _endorsements.GetOrAdd((e.Epoch, e.Checkpoint), _ => new())
            .TryAdd((e.Member.ToLowerInvariant(), $"{e.MerkleRoot}/{e.BlobAddress}/{e.SampleCount}".ToLowerInvariant()), e);

    public bool Add(RecordsSignature r) =>
        _records.GetOrAdd((r.Epoch, r.ChunkIndex), _ => new()).TryAdd((r.Member.ToLowerInvariant(), r.Digest.ToLowerInvariant()), r);

    public IReadOnlyList<CheckpointEndorsement> Endorsements(ulong epoch, ushort checkpoint) =>
        _endorsements.TryGetValue((epoch, checkpoint), out var d) ? [.. d.Values] : [];

    public IReadOnlyList<RecordsSignature> RecordSignatures(ulong epoch, int chunk) =>
        _records.TryGetValue((epoch, chunk), out var d) ? [.. d.Values] : [];

    public void PruneBefore(ulong epoch)
    {
        foreach (var key in _endorsements.Keys.Where(k => k.Item1 < epoch))
        {
            _endorsements.TryRemove(key, out _);
        }

        foreach (var key in _records.Keys.Where(k => k.Item1 < epoch))
        {
            _records.TryRemove(key, out _);
        }
    }
}
