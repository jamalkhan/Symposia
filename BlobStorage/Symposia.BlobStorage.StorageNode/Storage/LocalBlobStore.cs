using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;

namespace Symposia.BlobStorage.StorageNode.Storage;

/// <summary>
/// Blobs as files on local disk, content-addressed by SHA-256 CID, git-style sharded directories.
/// See Requirements/BlobStorage/metadata-architecture.md and Requirements/Network/node-architecture-and-storage.md.
/// </summary>
public sealed class LocalBlobStore
{
    private const int BufferSize = 81920;

    private readonly string _root;
    private readonly string _tempDir;

    public LocalBlobStore(IOptions<StorageNodeOptions> options)
    {
        _root = Path.GetFullPath(options.Value.StorageRoot);
        _tempDir = Path.Combine(_root, "tmp");
    }

    public void EnsureRootExists()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_tempDir);
    }

    public bool IsRootAccessible()
    {
        try
        {
            var probePath = Path.Combine(_tempDir, $".probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probePath, []);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Streams <paramref name="content"/> to a temp file, fsyncs it durably, then atomically renames it
    /// to its content-addressed path. The CID is not known until the stream is fully consumed and hashed,
    /// so the write is never acknowledged before the data is both hashed and durable.
    /// </summary>
    public async Task<(Cid Cid, long SizeBytes)> WriteAsync(Stream content, CancellationToken cancellationToken) =>
        (await WriteWithMerkleAsync(content, cancellationToken)) is var w ? (w.Cid, w.SizeBytes) : default;

    /// <summary>
    /// As <see cref="WriteAsync"/>, also returning the blob's Merkle root (#8 FR6.4). The leaf hashes are
    /// kept beside the blob (<c>&lt;cid&gt;.merkle</c>) so a challenge needs one leaf read, not a full rehash.
    /// </summary>
    public async Task<(Cid Cid, long SizeBytes, byte[] MerkleRoot)> WriteWithMerkleAsync(Stream content, CancellationToken cancellationToken)
    {
        var merkle = new MerkleBuilder();
        EnsureRootExists();

        var tempPath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.tmp");
        long sizeBytes = 0;

        try
        {
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var tempStream = new FileStream(
                    tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        hasher.AppendData(buffer, 0, read);
                        merkle.Append(buffer.AsSpan(0, read));
                        await tempStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        sizeBytes += read;
                    }

                    await tempStream.FlushAsync(cancellationToken);
                    // fsync: a node "durably acknowledges" a write only once flushed to persistent storage
                    // (Requirements/BlobStorage/write-quorum-and-consistency.md#write-quorum). FileStream has
                    // no async fsync overload, so the durable flush itself is synchronous.
                    tempStream.Flush(flushToDisk: true);
                }

                var cid = Cid.FromHash(hasher.GetHashAndReset());
                var finalPath = GetFullPath(cid);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

                if (File.Exists(finalPath))
                {
                    // Content-addressed dedup: identical bytes already durably stored under this CID.
                    File.Delete(tempPath);
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }

                var leaves = merkle.Finish();
                WriteLeaves(cid, leaves);
                return (cid, sizeBytes, MerkleTree.Root(leaves));
            }
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    public bool Exists(Cid cid) => File.Exists(GetFullPath(cid));

    public long GetLength(Cid cid) => new FileInfo(GetFullPath(cid)).Length;

    public FileStream OpenRead(Cid cid, long offset = 0)
    {
        var stream = new FileStream(
            GetFullPath(cid), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        return stream;
    }

    /// <summary>The blob's leaf hashes, rebuilt from the blob (and saved) when the sidecar is missing, e.g. for pre-#8 blobs.</summary>
    public async Task<IReadOnlyList<byte[]>> ReadLeavesAsync(Cid cid, CancellationToken ct)
    {
        var sidecar = GetFullPath(cid) + MerkleSuffix;
        if (File.Exists(sidecar))
        {
            var raw = await File.ReadAllBytesAsync(sidecar, ct);
            if (raw.Length % MerkleTree.HashSize == 0 && raw.Length > 0)
            {
                return Enumerable.Range(0, raw.Length / MerkleTree.HashSize)
                    .Select(i => raw.AsSpan(i * MerkleTree.HashSize, MerkleTree.HashSize).ToArray()).ToList();
            }
        }

        var builder = new MerkleBuilder();
        await using (var stream = OpenRead(cid))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                builder.Append(buffer.AsSpan(0, read));
            }
        }

        var leaves = builder.Finish();
        WriteLeaves(cid, leaves);
        return leaves;
    }

    private const string MerkleSuffix = ".merkle";

    private void WriteLeaves(Cid cid, IReadOnlyList<byte[]> leaves)
    {
        var sidecar = GetFullPath(cid) + MerkleSuffix;
        var temp = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.merkle.tmp");
        File.WriteAllBytes(temp, leaves.SelectMany(l => l).ToArray());
        File.Move(temp, sidecar, overwrite: true);
    }

    public bool Delete(Cid cid)
    {
        var path = GetFullPath(cid);
        File.Delete(path + MerkleSuffix);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Enumerates all CIDs stored on disk by walking the sharded directory tree.
    /// Skips the tmp directory and any non-hex filenames.
    /// </summary>
    public IEnumerable<Cid> EnumerateCids()
    {
        if (!Directory.Exists(_root)) yield break;

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            // Skip anything inside the tmp directory.
            if (file.StartsWith(_tempDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.EndsWith(MerkleSuffix, StringComparison.Ordinal)) continue; // a blob's leaf hashes, not a blob

            var name = Path.GetFileNameWithoutExtension(file);
            if (Cid.TryParse(name, out var cid)) yield return cid;
        }
    }

    private string GetFullPath(Cid cid) => Path.Combine(_root, cid.ToShardedRelativePath());
}
