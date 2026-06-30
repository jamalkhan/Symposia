using System.Security.Cryptography;
using System.Text;

namespace Symposia.BlobStorage.Domain;

/// <summary>
/// Content identifier for a blob: the lowercase hex SHA-256 digest of its bytes.
/// See Requirements/BlobStorage/redundancy-and-data-integrity.md#cryptographic-data-integrity.
/// </summary>
public readonly struct Cid : IEquatable<Cid>
{
    private const int Sha256HexLength = 64;

    public string Value { get; }

    private Cid(string value)
    {
        Value = value;
    }

    public static Cid Parse(string value)
    {
        if (!TryParse(value, out var cid))
        {
            throw new FormatException($"'{value}' is not a valid 64-character lowercase hex SHA-256 CID.");
        }

        return cid;
    }

    public static bool TryParse(string? value, out Cid cid)
    {
        if (value is { Length: Sha256HexLength } && IsLowercaseHex(value))
        {
            cid = new Cid(value);
            return true;
        }

        cid = default;
        return false;
    }

    public static Cid FromHash(ReadOnlySpan<byte> sha256Digest)
    {
        if (sha256Digest.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException($"Expected a {SHA256.HashSizeInBytes}-byte SHA-256 digest.", nameof(sha256Digest));
        }

        return new Cid(Convert.ToHexStringLower(sha256Digest));
    }

    /// <summary>
    /// Sharded relative path (git-style) so a single directory never holds millions of files:
    /// "ab/cd/abcd...64hexchars.blob".
    /// </summary>
    public string ToShardedRelativePath()
    {
        return Path.Combine(Value[..2], Value[2..4], $"{Value}.blob");
    }

    public bool Equals(Cid other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Cid other && Equals(other);

    public override int GetHashCode() => Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    public override string ToString() => Value;

    private static bool IsLowercaseHex(string value)
    {
        foreach (var c in value)
        {
            var isLowerHexDigit = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isLowerHexDigit)
            {
                return false;
            }
        }

        return true;
    }
}
