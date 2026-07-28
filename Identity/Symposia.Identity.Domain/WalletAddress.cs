using System.Text.RegularExpressions;

namespace Symposia.Identity.Domain;

/// <summary>
/// A canonical (lowercase, "0x"-prefixed, 20-byte) EVM wallet address — the
/// sole root identifier for a Symposia network identity (issue #21, FR2).
/// </summary>
public readonly partial struct WalletAddress : IEquatable<WalletAddress>
{
    [GeneratedRegex("^0x[0-9a-fA-F]{40}$")]
    private static partial Regex Pattern();

    public string Value { get; }

    private WalletAddress(string normalized)
    {
        Value = normalized;
    }

    /// <summary>
    /// Parses and normalizes a wallet address. Throws <see cref="FormatException"/>
    /// for anything that isn't a well-formed 20-byte hex address (TC-6.5).
    /// </summary>
    public static WalletAddress Parse(string raw)
    {
        if (raw is null || !Pattern().IsMatch(raw))
        {
            throw new FormatException($"'{raw}' is not a valid wallet address");
        }

        return new WalletAddress(raw.ToLowerInvariant());
    }

    public static bool TryParse(string raw, out WalletAddress address)
    {
        if (raw is not null && Pattern().IsMatch(raw))
        {
            address = new WalletAddress(raw.ToLowerInvariant());
            return true;
        }

        address = default;
        return false;
    }

    public bool Equals(WalletAddress other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is WalletAddress other && Equals(other);

    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    public override string ToString() => Value;
}
