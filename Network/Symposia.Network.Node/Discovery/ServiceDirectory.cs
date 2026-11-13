using System.Numerics;
using System.Text.Json;
using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node.Discovery;

/// <summary>
/// Where a node's Node Services listen (#225 FR2.2), served at <see cref="Path"/> on its on-chain primary endpoint and
/// signed by its node key. The primary endpoint is node-signed on-chain (#129), the directory off-chain, so no third
/// party can redirect a Verifier's probes. Bound to chain and registry (no cross-network replay); <see cref="Seq"/>
/// is the issue time in ms, and a Verifier never accepts a lower one than it has seen (no rollback).
/// </summary>
public sealed record ServiceDirectory(
    int V, ulong ChainId, string NodeRegistry, string Node, long Seq, long IssuedAt, long ExpiresAt,
    IReadOnlyList<ServiceDirectory.Entry> Services, string Signature)
{
    public const string Path = "/.well-known/symposia/services";

    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public sealed record Entry(int Id, string Url);

    private static readonly JsonSerializerOptions Canonical = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The signed bytes: an ABI encoding (no JSON canonicalization ambiguity) over the sorted entries.</summary>
    public static byte[] Digest(ulong chainId, string nodeRegistry, string node, long seq, long issuedAt, long expiresAt, IEnumerable<Entry> services)
    {
        var entries = JsonSerializer.Serialize(services.OrderBy(e => e.Id).ToList(), Canonical);
        return Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncoded(
            new ABIValue("string", "symposia.services.v1"), new ABIValue("uint256", new BigInteger(chainId)), new ABIValue("address", nodeRegistry),
            new ABIValue("address", node), new ABIValue("uint256", new BigInteger(seq)), new ABIValue("uint256", new BigInteger(issuedAt)),
            new ABIValue("uint256", new BigInteger(expiresAt)), new ABIValue("bytes32", Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes(entries)))));
    }

    public static ServiceDirectory Sign(EthECKey key, ulong chainId, string nodeRegistry, IEnumerable<Entry> services, DateTimeOffset now)
    {
        var sorted = services.OrderBy(e => e.Id).ToList();
        var node = key.GetPublicAddress();
        var (issued, expires) = (now.ToUnixTimeSeconds(), (now + Ttl).ToUnixTimeSeconds());
        var seq = now.ToUnixTimeMilliseconds();
        var signature = new EthereumMessageSigner().Sign(Digest(chainId, nodeRegistry, node, seq, issued, expires, sorted), key);
        return new ServiceDirectory(1, chainId, nodeRegistry, node, seq, issued, expires, sorted, signature);
    }

    /// <summary>Null when valid for <paramref name="node"/> on this network at <paramref name="now"/>; otherwise why not.</summary>
    public string? Problem(string node, ulong chainId, string nodeRegistry, DateTimeOffset now, long minSeq = 0)
    {
        if (V != 1) return $"unknown directory version {V}";
        if (!string.Equals(Node, node, StringComparison.OrdinalIgnoreCase)) return "directory is for another node";
        if (ChainId != chainId || !string.Equals(NodeRegistry, nodeRegistry, StringComparison.OrdinalIgnoreCase)) return "directory is for another network";
        if (now.ToUnixTimeSeconds() > ExpiresAt) return "directory expired";
        if (Seq < minSeq) return "directory is older than one already seen";

        try
        {
            var signer = new EthereumMessageSigner().EcRecover(Digest(chainId, nodeRegistry, Node, Seq, IssuedAt, ExpiresAt, Services), Signature);
            return string.Equals(signer, node, StringComparison.OrdinalIgnoreCase) ? null : "directory isn't signed by the node";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return "directory signature is malformed";
        }
    }

    public string? UrlOf(int service) => Services.FirstOrDefault(e => e.Id == service)?.Url;
}
