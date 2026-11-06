using System.Text.Json;
using System.Text.Json.Serialization;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;

namespace Symposia.Network.Node.Telemetry;

/// <summary>A decoded, verified checkpoint batch.</summary>
public sealed record DecodedBatch(
    ulong Epoch,
    ushort Checkpoint,
    IReadOnlyList<SignedTelemetry<HeartbeatSample>> Heartbeats,
    IReadOnlyList<SignedTelemetry<Attestation>> Attestations,
    byte[] MerkleRoot)
{
    public uint SampleCount => (uint)(Heartbeats.Count + Attestations.Count);
}

/// <summary>
/// The one canonical byte encoding of a checkpoint window's raw signed batch (issue #158). The
/// blob address committed on-chain is <c>keccak256</c> of these bytes, so every committee member
/// must produce identical bytes for the same sample set: items are ordered exactly as
/// <see cref="TelemetryMerkle"/> orders leaves, and JSON is written with fixed options.
/// Decoding re-verifies every signature, the window each item claims, and the Merkle root.
/// </summary>
public static class TelemetryBatchCodec
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    private sealed record Wire(int Version, ulong Epoch, ushort Checkpoint, List<SignedTelemetry<HeartbeatSample>> Heartbeats,
        List<SignedTelemetry<Attestation>> Attestations);

    public static byte[] Encode(
        ulong epoch,
        ushort checkpoint,
        IEnumerable<SignedTelemetry<HeartbeatSample>> heartbeats,
        IEnumerable<SignedTelemetry<Attestation>> attestations) =>
        JsonSerializer.SerializeToUtf8Bytes(new Wire(Version, epoch, checkpoint, OrderHeartbeats(heartbeats), OrderAttestations(attestations)), Json);

    /// <summary>Content address of an encoded batch: keccak256 of its bytes.</summary>
    public static byte[] Address(byte[] encoded) => Sha3Keccack.Current.CalculateHash(encoded);

    /// <summary>
    /// Decodes and verifies <paramref name="encoded"/>. Throws <see cref="InvalidDataException"/> if the
    /// bytes aren't canonical, any item is unsigned, from another epoch or window, or duplicated.
    /// </summary>
    public static DecodedBatch Decode(byte[] encoded, TelemetrySchedule schedule, ulong chainId, string registry)
    {
        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(encoded, Json) ?? throw new InvalidDataException("Empty batch.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Batch is not valid JSON.", ex);
        }

        if (wire.Version != Version)
        {
            throw new InvalidDataException($"Unsupported batch version {wire.Version}.");
        }

        foreach (var h in wire.Heartbeats)
        {
            if (h.Payload.EpochIndex != wire.Epoch || h.Payload.SampleIndex >= schedule.SlotsPerEpoch
                || schedule.CheckpointOf(h.Payload.SampleIndex) != wire.Checkpoint || !TelemetrySigning.Verify(h, chainId, registry))
            {
                throw new InvalidDataException($"Heartbeat {h.Payload.ReplayKey} doesn't belong in window {wire.Epoch}/{wire.Checkpoint} or isn't validly signed.");
            }
        }

        foreach (var a in wire.Attestations)
        {
            if (a.Payload.EpochIndex != wire.Epoch || a.Payload.SampleIndex >= schedule.SlotsPerEpoch
                || schedule.CheckpointOf(a.Payload.SampleIndex) != wire.Checkpoint || !TelemetrySigning.Verify(a, chainId, registry))
            {
                throw new InvalidDataException($"Attestation {a.Payload.ReplayKey} doesn't belong in window {wire.Epoch}/{wire.Checkpoint} or isn't validly signed.");
            }
        }

        if (!Encode(wire.Epoch, wire.Checkpoint, wire.Heartbeats, wire.Attestations).AsSpan().SequenceEqual(encoded))
        {
            throw new InvalidDataException("Batch bytes are not in canonical form.");
        }

        if (wire.Heartbeats.Count + wire.Attestations.Count == 0)
        {
            throw new InvalidDataException("Batch is empty.");
        }

        var root = TelemetryMerkle.Build(wire.Heartbeats, wire.Attestations, chainId, registry).Root;
        return new DecodedBatch(wire.Epoch, wire.Checkpoint, wire.Heartbeats, wire.Attestations, root);
    }

    /// <summary>Decodes and checks the bytes against what's committed on-chain.</summary>
    public static DecodedBatch DecodeCommitted(byte[] encoded, byte[] blobAddress, byte[] merkleRoot, TelemetrySchedule schedule,
        ulong chainId, string registry)
    {
        if (!Address(encoded).AsSpan().SequenceEqual(blobAddress))
        {
            throw new InvalidDataException($"Blob content hash doesn't match {blobAddress.ToHex(true)}.");
        }

        var batch = Decode(encoded, schedule, chainId, registry);
        if (!batch.MerkleRoot.AsSpan().SequenceEqual(merkleRoot))
        {
            throw new InvalidDataException("Batch Merkle root doesn't match the committed root.");
        }

        return batch;
    }

    private static List<SignedTelemetry<HeartbeatSample>> OrderHeartbeats(IEnumerable<SignedTelemetry<HeartbeatSample>> items) =>
        items.DistinctBy(s => (s.Payload, s.Signature))
            .OrderBy(s => s.Payload.NodeId.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(s => s.Payload.EpochIndex).ThenBy(s => s.Payload.SampleIndex)
            .ThenBy(s => s.Signature, StringComparer.Ordinal)
            .ToList();

    private static List<SignedTelemetry<Attestation>> OrderAttestations(IEnumerable<SignedTelemetry<Attestation>> items) =>
        items.DistinctBy(a => (a.Payload, a.Signature))
            .OrderBy(a => a.Payload.VerifierId.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(a => a.Payload.TargetNodeId.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(a => a.Payload.EpochIndex).ThenBy(a => a.Payload.SampleIndex)
            .ThenBy(a => a.Signature, StringComparer.Ordinal)
            .ToList();
}
