using System.Security.Cryptography;
using System.Text;

namespace Symposia.Identity.Domain;

/// <summary>
/// Derives the <c>symposia_identity_id</c> deterministically from a wallet
/// address, per the Arch pass on issue #21: <c>UUIDv5(fixed_namespace,
/// lowercase address)</c>. This keeps the id reconstructible from the wallet
/// address alone, with no lookup required — the Postgres binding table exists
/// for reverse resolution and query convenience, not as the source of truth
/// (AC1: "deterministically derivable from / bound to its public address").
/// </summary>
public static class SymposiaIdentityId
{
    // Fixed, project-specific namespace UUID (v4, generated once and frozen —
    // must never change, or every existing identity_id would shift).
    private static readonly Guid Namespace = Guid.Parse("6d9b3f0a-9d0e-4b7b-9a3a-6f8f1c9d2e40");

    public static Guid Derive(WalletAddress wallet) => DeriveV5(Namespace, wallet.Value);

    /// <summary>RFC 4122 §4.3 UUID version 5 (SHA-1 name-based) derivation.</summary>
    private static Guid DeriveV5(Guid @namespace, string name)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        @namespace.TryWriteBytes(namespaceBytes);
        SwapGuidByteOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input);
        nameBytes.CopyTo(input.AsSpan(namespaceBytes.Length));

        var hash = SHA1.HashData(input);
        var result = hash[..16];

        result[6] = (byte)((result[6] & 0x0F) | 0x50); // version 5
        result[8] = (byte)((result[8] & 0x3F) | 0x80); // RFC 4122 variant

        SwapGuidByteOrder(result);
        return new Guid(result);
    }

    // .NET's Guid byte layout for the first three fields is little-endian on
    // the wire; RFC 4122 requires big-endian. Swap so the derivation matches
    // the standard algorithm bit-for-bit.
    private static void SwapGuidByteOrder(Span<byte> guidBytes)
    {
        (guidBytes[0], guidBytes[3]) = (guidBytes[3], guidBytes[0]);
        (guidBytes[1], guidBytes[2]) = (guidBytes[2], guidBytes[1]);
        (guidBytes[4], guidBytes[5]) = (guidBytes[5], guidBytes[4]);
        (guidBytes[6], guidBytes[7]) = (guidBytes[7], guidBytes[6]);
    }
}
