using Nethereum.Util;

namespace Symposia.Network.Node.Telemetry;

/// <summary>One checkpoint window's committed batch: the root, the canonically ordered leaves, and the sample count.</summary>
public sealed record TelemetryBatch(byte[] Root, IReadOnlyList<byte[]> Leaves);

/// <summary>
/// Merkle commitments over a checkpoint window's signed samples (issue #75). Items are put in a
/// canonical order (signer, epoch, slot, then signature — never arrival order), hashed to leaves
/// with <see cref="TelemetrySigning.Leaf"/>, and combined with OpenZeppelin's sorted-pair keccak
/// convention, so <c>TelemetryCheckpointRegistry.verifySampleInclusion</c> (MerkleProof) accepts
/// proofs built here. An odd node at any level is carried up unchanged.
/// </summary>
public static class TelemetryMerkle
{
    public static TelemetryBatch Build(
        IEnumerable<SignedTelemetry<HeartbeatSample>> samples,
        IEnumerable<SignedTelemetry<Attestation>> attestations,
        ulong chainId,
        string registry)
    {
        var heartbeatLeaves = samples
            .OrderBy(s => s.Payload.NodeId.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(s => s.Payload.EpochIndex).ThenBy(s => s.Payload.SampleIndex)
            .ThenBy(s => s.Signature, StringComparer.Ordinal)
            .Select(s => TelemetrySigning.Leaf(TelemetrySigning.Digest(s.Payload, chainId, registry), s.Signature));
        var attestationLeaves = attestations
            .OrderBy(a => a.Payload.VerifierId.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(a => a.Payload.TargetNodeId.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(a => a.Payload.EpochIndex).ThenBy(a => a.Payload.SampleIndex)
            .ThenBy(a => a.Signature, StringComparer.Ordinal)
            .Select(a => TelemetrySigning.Leaf(TelemetrySigning.Digest(a.Payload, chainId, registry), a.Signature));
        var leaves = heartbeatLeaves.Concat(attestationLeaves).ToList();
        return new TelemetryBatch(Root(leaves), leaves);
    }

    public static byte[] Root(IReadOnlyList<byte[]> leaves)
    {
        if (leaves.Count == 0)
        {
            throw new ArgumentException("A checkpoint needs at least one leaf.", nameof(leaves));
        }

        var level = leaves.ToList();
        while (level.Count > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                next.Add(i + 1 < level.Count ? HashPair(level[i], level[i + 1]) : level[i]);
            }

            level = next;
        }

        return level[0];
    }

    /// <summary>Sibling path for the leaf at <paramref name="index"/>, bottom-up.</summary>
    public static IReadOnlyList<byte[]> Proof(IReadOnlyList<byte[]> leaves, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, leaves.Count);
        var proof = new List<byte[]>();
        var level = leaves.ToList();
        while (level.Count > 1)
        {
            var sibling = index ^ 1;
            if (sibling < level.Count)
            {
                proof.Add(level[sibling]);
            }

            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                next.Add(i + 1 < level.Count ? HashPair(level[i], level[i + 1]) : level[i]);
            }

            level = next;
            index /= 2;
        }

        return proof;
    }

    public static bool Verify(IReadOnlyList<byte[]> proof, byte[] root, byte[] leaf) =>
        proof.Aggregate(leaf, HashPair).AsSpan().SequenceEqual(root);

    public static byte[] HashPair(byte[] a, byte[] b) =>
        Sha3Keccack.Current.CalculateHash(a.AsSpan().SequenceCompareTo(b) <= 0 ? [.. a, .. b] : [.. b, .. a]);
}
