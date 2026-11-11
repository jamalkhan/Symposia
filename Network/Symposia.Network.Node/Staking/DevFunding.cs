using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node.Staking;

/// <summary>Body of <see cref="DevFunding.FundPath"/>: the services to fund, each with the capacity it will stake for.</summary>
public sealed record DevFundRequest(string Node, IReadOnlyList<DevFundService> Services, long UnixSeconds, string Signature);

public sealed record DevFundService(byte Service, string Capacity);

/// <summary>Body of <see cref="DevFunding.VerifyPath"/>.</summary>
public sealed record DevVerifyRequest(string Node, byte Service, long UnixSeconds, string Signature);

/// <summary>
/// LocalDev funding and verification for self-staking (issue #200). A joining host signs, with its
/// node key (EIP-191), a digest binding the network, the action, the node and a timestamp; the
/// LocalDev gateway checks it and then mints dev SYM, sends gas, or records a passing verification
/// with the anvil dev key, which never leaves that gateway. Both actions are idempotent, so a replay
/// within the freshness window changes nothing.
/// </summary>
public static class DevFunding
{
    public const string Domain = "Symposia.DevFunding.v1";
    public const string FundPath = "/v1/dev/fund";
    public const string VerifyPath = "/v1/dev/verify";
    public const ulong LocalDevChainId = 31337;
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(60);

    private static readonly EthereumMessageSigner MessageSigner = new();

    public static byte[] FundDigest(ulong chainId, string node, IEnumerable<DevFundService> services, long unixSeconds) =>
        Digest(chainId, "fund", node, string.Join(",", services.Select(s => $"{s.Service}:{s.Capacity}")), unixSeconds);

    public static byte[] VerifyDigest(ulong chainId, string node, byte service, long unixSeconds) =>
        Digest(chainId, "verify", node, service.ToString(), unixSeconds);

    public static string Sign(EthECKey key, byte[] digest) => MessageSigner.Sign(digest, key);

    /// <summary>Null when <paramref name="signature"/> is <paramref name="node"/>'s, fresh, over <paramref name="digest"/>; otherwise why not.</summary>
    public static string? Validate(string node, long unixSeconds, string signature, byte[] digest, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(node) || !AddressUtil.Current.IsValidEthereumAddressHexFormat(node) || string.IsNullOrEmpty(signature))
        {
            return "missing or malformed node";
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - unixSeconds) > MaxSkew.TotalSeconds)
        {
            return "stale or future timestamp";
        }

        try
        {
            return string.Equals(MessageSigner.EcRecover(digest, signature), node, StringComparison.OrdinalIgnoreCase)
                ? null
                : "signature does not match node";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return "malformed signature";
        }
    }

    private static byte[] Digest(ulong chainId, string action, string node, string detail, long unixSeconds) =>
        Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncodedPacked(
            new ABIValue("string", Domain),
            new ABIValue("uint256", chainId),
            new ABIValue("string", action),
            new ABIValue("address", node),
            new ABIValue("string", detail),
            new ABIValue("uint64", (ulong)unixSeconds)));
}
