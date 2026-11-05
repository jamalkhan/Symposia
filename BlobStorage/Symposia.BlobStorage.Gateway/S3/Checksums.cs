using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Symposia.BlobStorage.Gateway.S3;

/// <summary>An S3 request rejected for its content (issue #139); rendered as an S3 XML error.</summary>
public sealed class S3RequestRejectedException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;
}

/// <summary>An incremental hash whose result is compared to what the client declared.</summary>
public interface IChecksum
{
    void Append(ReadOnlySpan<byte> data);

    byte[] Result();
}

/// <summary>
/// The checksum algorithms S3 clients send (<c>x-amz-checksum-&lt;alg&gt;</c>), plus MD5 for
/// <c>Content-MD5</c>. Values travel base64-encoded, big-endian for CRCs.
/// </summary>
public static class Checksums
{
    public static readonly IReadOnlyList<string> Algorithms = ["crc32", "crc32c", "crc64nvme", "sha1", "sha256"];

    /// <summary>Null for an unsupported algorithm name.</summary>
    public static IChecksum? Create(string algorithm) => algorithm.ToLowerInvariant() switch
    {
        "crc32" => new Crc32(0xEDB88320u),
        "crc32c" => new Crc32(0x82F63B78u),
        "crc64nvme" => new Crc64(0x9A6C9329AC4BC9B5ul),
        "sha1" => new Hash(HashAlgorithmName.SHA1),
        "sha256" => new Hash(HashAlgorithmName.SHA256),
        "md5" => new Hash(HashAlgorithmName.MD5),
        _ => null,
    };

    /// <summary>Base64 of the checksum of <paramref name="data"/>, as clients send it.</summary>
    public static string ComputeBase64(string algorithm, ReadOnlySpan<byte> data)
    {
        var checksum = Create(algorithm) ?? throw new ArgumentException($"Unsupported checksum {algorithm}.", nameof(algorithm));
        checksum.Append(data);
        return Convert.ToBase64String(checksum.Result());
    }

    private sealed class Hash(HashAlgorithmName name) : IChecksum
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(name);

        public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);

        public byte[] Result() => _hash.GetHashAndReset();
    }

    /// <summary>Reflected table-driven CRC-32 (IEEE 0xEDB88320 or Castagnoli 0x82F63B78).</summary>
    private sealed class Crc32 : IChecksum
    {
        private static readonly Dictionary<uint, uint[]> Tables = [];
        private readonly uint[] _table;
        private uint _crc = 0xFFFFFFFFu;

        public Crc32(uint polynomial)
        {
            lock (Tables)
            {
                if (!Tables.TryGetValue(polynomial, out _table!))
                {
                    _table = new uint[256];
                    for (uint i = 0; i < 256; i++)
                    {
                        var c = i;
                        for (var k = 0; k < 8; k++)
                        {
                            c = (c & 1) != 0 ? (c >> 1) ^ polynomial : c >> 1;
                        }

                        _table[i] = c;
                    }

                    Tables[polynomial] = _table;
                }
            }
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            var crc = _crc;
            foreach (var b in data)
            {
                crc = _table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            _crc = crc;
        }

        public byte[] Result()
        {
            var result = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(result, ~_crc);
            return result;
        }
    }

    /// <summary>Reflected table-driven CRC-64 (NVMe polynomial 0x9A6C9329AC4BC9B5, as used by S3's CRC64NVME).</summary>
    private sealed class Crc64 : IChecksum
    {
        private static readonly Dictionary<ulong, ulong[]> Tables = [];
        private readonly ulong[] _table;
        private ulong _crc = ulong.MaxValue;

        public Crc64(ulong polynomial)
        {
            lock (Tables)
            {
                if (!Tables.TryGetValue(polynomial, out _table!))
                {
                    _table = new ulong[256];
                    for (ulong i = 0; i < 256; i++)
                    {
                        var c = i;
                        for (var k = 0; k < 8; k++)
                        {
                            c = (c & 1) != 0 ? (c >> 1) ^ polynomial : c >> 1;
                        }

                        _table[i] = c;
                    }

                    Tables[polynomial] = _table;
                }
            }
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            var crc = _crc;
            foreach (var b in data)
            {
                crc = _table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            _crc = crc;
        }

        public byte[] Result()
        {
            var result = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(result, ~_crc);
            return result;
        }
    }
}
