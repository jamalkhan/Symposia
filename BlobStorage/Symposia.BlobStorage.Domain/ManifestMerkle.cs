using System.Buffers.Binary;
using System.Text;

namespace Symposia.BlobStorage.Domain;

/// <summary>One attested holding: <paramref name="Cid"/> held for <paramref name="TenantId"/> ("" when no reference is known).</summary>
public sealed record ManifestLeaf(Cid Cid, long SizeBytes, string TenantId);

/// <summary>
/// The epoch Merkle root a storage node submits on-chain over its manifest (#12 Layer 3, FR3.1), and the
/// inclusion proofs checked against it. One reference implementation for nodes, gateways and verifiers, so a
/// root recomputed anywhere matches byte for byte (QA TC3.2).
/// </summary>
/// <remarks>
/// A leaf is one <c>{cid, size, tenant_id, checksum}</c> tuple. The CID is the SHA-256 of the content, so it
/// is the checksum too and isn't repeated. Its data is <c>cid (32 bytes) ‖ size (int64 big-endian) ‖ tenant_id (UTF-8)</c>:
/// fixed-width fields first, so the encoding is unambiguous. A blob several tenants reference (cross-tenant
/// dedup) has one leaf per tenant. Leaves are sorted by CID, then tenant (ordinal), and hashed with
/// <see cref="MerkleTree"/>'s domain-separated leaf and node hashes. An empty manifest has the root of one
/// empty leaf, never zero, which EpochRootRegistry treats as "no submission".
/// </remarks>
public static class ManifestMerkle
{
    /// <summary>Longest tenant ID a leaf carries; the leaf must fit <see cref="MerkleTree.LeafSize"/>.</summary>
    public const int MaxTenantIdBytes = 1024;

    public static byte[] LeafData(ManifestLeaf leaf)
    {
        var tenant = Encoding.UTF8.GetBytes(leaf.TenantId);
        if (tenant.Length > MaxTenantIdBytes)
        {
            throw new ArgumentException($"Tenant ID is longer than {MaxTenantIdBytes} bytes.", nameof(leaf));
        }

        var data = new byte[MerkleTree.HashSize + sizeof(long) + tenant.Length];
        Convert.FromHexString(leaf.Cid.Value).CopyTo(data, 0);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(MerkleTree.HashSize), leaf.SizeBytes);
        tenant.CopyTo(data, MerkleTree.HashSize + sizeof(long));
        return data;
    }

    /// <summary>The canonical leaf order.</summary>
    public static List<ManifestLeaf> Sort(IEnumerable<ManifestLeaf> leaves) =>
        [.. leaves.OrderBy(l => l.Cid.Value, StringComparer.Ordinal).ThenBy(l => l.TenantId, StringComparer.Ordinal)];

    /// <summary>Leaf hashes of <paramref name="sorted"/>, which must already be in <see cref="Sort"/> order.</summary>
    public static List<byte[]> LeafHashes(IReadOnlyList<ManifestLeaf> sorted) => [.. sorted.Select(l => MerkleTree.LeafHash(LeafData(l)))];

    public static byte[] Root(IEnumerable<ManifestLeaf> leaves) => MerkleTree.Root(LeafHashes(Sort(leaves)));

    /// <summary>Whether <paramref name="leaf"/> at <paramref name="index"/> of <paramref name="leafCount"/> proves against <paramref name="root"/>.</summary>
    public static bool Verify(ManifestLeaf leaf, long index, long leafCount, IReadOnlyList<byte[]> siblings, ReadOnlySpan<byte> root) =>
        Encoding.UTF8.GetByteCount(leaf.TenantId) <= MaxTenantIdBytes &&
        MerkleTree.Verify(LeafData(leaf), index, leafCount, siblings, root);
}
