using System.Security.Cryptography;

namespace Symposia.BlobStorage.Domain;

/// <summary>
/// The Merkle tree a storage node keeps over each blob, for proof-of-storage challenges (#8 FR6.4): a
/// node proves it holds a randomly chosen 64 KiB leaf by returning the leaf and its sibling hashes, and
/// the verifier recomputes the root recorded at write time, without the whole blob ever crossing the wire.
/// </summary>
/// <remarks>
/// Leaves are SHA-256(0x00 ‖ data) and inner nodes SHA-256(0x01 ‖ left ‖ right), so a leaf can never be
/// passed off as an inner node. An odd node out at any level is carried up unchanged. An empty blob has
/// one empty leaf.
/// </remarks>
public static class MerkleTree
{
    public const int LeafSize = 64 * 1024;
    public const int HashSize = 32;

    public static byte[] LeafHash(ReadOnlySpan<byte> data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([0x00]);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    private static byte[] NodeHash(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([0x01]);
        hash.AppendData(left);
        hash.AppendData(right);
        return hash.GetHashAndReset();
    }

    /// <summary>The root over <paramref name="leaves"/> (leaf hashes, in order).</summary>
    public static byte[] Root(IReadOnlyList<byte[]> leaves)
    {
        if (leaves.Count == 0)
        {
            return LeafHash([]);
        }

        var level = leaves.ToList();
        while (level.Count > 1)
        {
            level = Up(level);
        }

        return level[0];
    }

    /// <summary>
    /// Sibling hashes from <paramref name="index"/>'s leaf to the root, bottom-up. An empty entry marks a
    /// level where the node had no sibling and was carried up.
    /// </summary>
    public static IReadOnlyList<byte[]> Proof(IReadOnlyList<byte[]> leaves, long index)
    {
        if (index < 0 || index >= Math.Max(1, leaves.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var proof = new List<byte[]>();
        var level = leaves.Count == 0 ? [LeafHash([])] : leaves.ToList();
        var i = (int)index;
        while (level.Count > 1)
        {
            var sibling = i ^ 1;
            proof.Add(sibling < level.Count ? level[sibling] : []);
            level = Up(level);
            i /= 2;
        }

        return proof;
    }

    /// <summary>Whether <paramref name="leafData"/> at <paramref name="index"/> of <paramref name="leafCount"/> proves against <paramref name="root"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> leafData, long index, long leafCount, IReadOnlyList<byte[]> siblings, ReadOnlySpan<byte> root)
    {
        if (index < 0 || index >= Math.Max(1, leafCount) || leafData.Length > LeafSize || root.Length != HashSize)
        {
            return false;
        }

        var hash = LeafHash(leafData);
        var i = index;
        var width = Math.Max(1, leafCount);
        var level = 0;
        while (width > 1)
        {
            if (level >= siblings.Count)
            {
                return false;
            }

            var sibling = siblings[level];
            var hasSibling = (i ^ 1) < width;
            if (sibling.Length != (hasSibling ? HashSize : 0))
            {
                return false; // the proof's shape doesn't match the tree's
            }

            if (hasSibling)
            {
                hash = (i & 1) == 0 ? NodeHash(hash, sibling) : NodeHash(sibling, hash);
            }

            i /= 2;
            width = (width + 1) / 2;
            level++;
        }

        return level == siblings.Count && CryptographicOperations.FixedTimeEquals(hash, root);
    }

    /// <summary>Leaves needed for a blob of <paramref name="sizeBytes"/> (at least one).</summary>
    public static long LeafCount(long sizeBytes) => Math.Max(1, (sizeBytes + LeafSize - 1) / LeafSize);

    private static List<byte[]> Up(List<byte[]> level)
    {
        var next = new List<byte[]>((level.Count + 1) / 2);
        for (var i = 0; i < level.Count; i += 2)
        {
            next.Add(i + 1 < level.Count ? NodeHash(level[i], level[i + 1]) : level[i]);
        }

        return next;
    }
}

/// <summary>Accumulates leaf hashes while a blob streams through, 64 KiB at a time.</summary>
public sealed class MerkleBuilder
{
    private readonly byte[] _pending = new byte[MerkleTree.LeafSize];
    private int _filled;
    private readonly List<byte[]> _leaves = [];

    public void Append(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            var take = Math.Min(data.Length, MerkleTree.LeafSize - _filled);
            data[..take].CopyTo(_pending.AsSpan(_filled));
            _filled += take;
            data = data[take..];
            if (_filled == MerkleTree.LeafSize)
            {
                _leaves.Add(MerkleTree.LeafHash(_pending));
                _filled = 0;
            }
        }
    }

    /// <summary>The leaf hashes, the final partial leaf included; call once, at the end.</summary>
    public IReadOnlyList<byte[]> Finish()
    {
        if (_filled > 0 || _leaves.Count == 0)
        {
            _leaves.Add(MerkleTree.LeafHash(_pending.AsSpan(0, _filled)));
            _filled = 0;
        }

        return _leaves;
    }
}
