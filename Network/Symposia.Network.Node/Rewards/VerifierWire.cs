using System.Numerics;
using System.Security.Cryptography;
using Nethereum.ABI;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node.Rewards;

/// <summary>
/// The Blob Gateway probe (#225). The Verifier sends a fresh nonce, signed with the metric-signer key; the gateway
/// answers <c>keccak256(nonce ‖ canary)</c> after reading its canary through the storage read path. The canary's
/// content is derived from the gateway's node address, so the Verifier checks the answer without credentials.
/// Known limit: this proves the serving path answers freshly and correctly, not that the bytes came from storage
/// (a gateway can compute the canary); proof of possession is a Verifier duty for #227.
/// </summary>
public static class ProbeChallenge
{
    public const string Path = "/healthz/probe";

    public const string Header = "X-Symposia-Probe";

    public const int CanaryBytes = 1024;

    /// <summary>How far a challenge's timestamp may be from the gateway's clock.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(60);

    /// <summary>Where the canary lives: a bucket name S3 rules reject, so no tenant can create, read or list it.</summary>
    public const string CanaryBucket = ".symposia-system";

    public static string CanaryKey(string node) => $"canary/{node.ToLowerInvariant()}/v1";

    /// <summary>The canary's 1 KiB: keccak256 chained from ("symposia.canary.v1", node).</summary>
    public static byte[] Canary(string node)
    {
        var bytes = new byte[CanaryBytes];
        var block = Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("symposia.canary.v1:" + node.ToLowerInvariant()));
        for (var i = 0; i < CanaryBytes; i += 32)
        {
            block.CopyTo(bytes, i);
            block = Sha3Keccack.Current.CalculateHash(block);
        }

        return bytes;
    }

    public static string Answer(byte[] nonce, ReadOnlySpan<byte> canary) =>
        Sha3Keccack.Current.CalculateHash([.. nonce, .. canary]).ToHex(true);

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(16);

    /// <summary>What the Verifier signs: domain, the gateway it's probing, the time and the nonce.</summary>
    public static byte[] Digest(string gateway, long unixSeconds, byte[] nonce) => Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncoded(
        new ABIValue("string", "symposia.probe.v1"), new ABIValue("address", gateway), new ABIValue("uint256", new BigInteger(unixSeconds)),
        new ABIValue("bytes", nonce)));

    /// <summary>The header value: <c>ts.nonceHex.signatureHex</c>.</summary>
    public static string Format(long unixSeconds, byte[] nonce, string signature) => $"{unixSeconds}.{nonce.ToHex()}.{signature.RemoveHexPrefix()}";

    public static bool TryParse(string? header, out long unixSeconds, out byte[] nonce, out string signature)
    {
        (unixSeconds, nonce, signature) = (0, [], "");
        var parts = header?.Split('.') ?? [];
        if (parts.Length != 3 || !long.TryParse(parts[0], out unixSeconds) || parts[1].Length is 0 or > 64 || !parts[1].IsHex() || !parts[2].IsHex())
        {
            return false;
        }

        nonce = parts[1].HexToByteArray();
        signature = parts[2].EnsureHexPrefix();
        return true;
    }

    /// <summary>The address that signed a challenge, or "".</summary>
    public static string Recover(string gateway, long unixSeconds, byte[] nonce, string signature)
    {
        try
        {
            return new EthereumMessageSigner().EcRecover(Digest(gateway, unixSeconds, nonce), signature);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return "";
        }
    }

    public sealed record Response(string Answer);
}

/// <summary>
/// A gateway's own bytes-served figure for an epoch (#225 FR3.4), signed by its node key. Domain-separated from
/// metric reports and service directories, so a node signature can never be reused as a metric attestation.
/// </summary>
public static class EpochReportSignature
{
    public const string PathPrefix = "/healthz/epoch-report/";

    public sealed record Report(ulong ChainId, string Distributor, string Node, ulong Epoch, long BytesServed, long Requests, string Signature);

    public static byte[] Digest(ulong chainId, string distributor, string node, ulong epoch, long bytesServed, long requests) =>
        Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncoded(
            new ABIValue("string", "symposia.epoch-report.v1"), new ABIValue("uint256", new BigInteger(chainId)), new ABIValue("address", distributor),
            new ABIValue("address", node), new ABIValue("uint256", new BigInteger(epoch)), new ABIValue("uint256", new BigInteger(bytesServed)),
            new ABIValue("uint256", new BigInteger(requests))));

    public static Report Sign(EthECKey key, ulong chainId, string distributor, ulong epoch, long bytesServed, long requests)
    {
        var node = key.GetPublicAddress();
        return new Report(chainId, distributor, node, epoch, bytesServed, requests,
            new EthereumMessageSigner().Sign(Digest(chainId, distributor, node, epoch, bytesServed, requests), key));
    }

    /// <summary>Whether <paramref name="r"/> is signed by <paramref name="node"/> for this chain, distributor and epoch.</summary>
    public static bool IsValid(Report r, ulong chainId, string distributor, string node, ulong epoch)
    {
        if (r.ChainId != chainId || r.Epoch != epoch || r.BytesServed < 0 || r.Requests < 0
            || !string.Equals(r.Distributor, distributor, StringComparison.OrdinalIgnoreCase) || !string.Equals(r.Node, node, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var signer = new EthereumMessageSigner().EcRecover(Digest(chainId, distributor, node, epoch, r.BytesServed, r.Requests), r.Signature);
            return string.Equals(signer, node, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }
}

/// <summary>
/// Verifier attestations through the Blockchain Gateway relay (#227): one node-key-signed batch per Verifier per
/// epoch (the relay pays the gas), and a self-sync so a Verifier-only host (absent from the node registry) joins
/// the pool.
/// </summary>
public static class VerifierAttestationWire
{
    public const string Path = "/v1/relay/verifier-attestations";

    public const string SyncPath = "/v1/relay/verifier-sync";

    /// <summary>Items per relayed tx: about 45k gas each, so 100 is about 4.5M.</summary>
    public const int MaxItems = 100;

    /// <summary><paramref name="Values"/> are decimal strings (uint256).</summary>
    public sealed record ItemWire(string Target, byte Slot, IReadOnlyList<string> Values);

    /// <param name="Nonce">Must exceed the last one used for (epoch, verifier); the Verifier uses unix milliseconds.</param>
    public sealed record Request(ulong Epoch, string Verifier, ulong Nonce, IReadOnlyList<ItemWire> Items, string Signature);

    public sealed record SyncRequest(string Node);

    public static List<Blockchain.Contracts.AttestationItem> ToItems(IEnumerable<ItemWire> items) =>
        [.. items.Select(i => new Blockchain.Contracts.AttestationItem { Target = i.Target, Slot = i.Slot, Values = [.. i.Values.Select(BigInteger.Parse)] })];

    /// <summary>Signs a batch with the Verifier's node key, as <c>VerifierAssignments.attest</c> checks.</summary>
    public static string Sign(EthECKey nodeKey, string contract, ulong chainId, ulong epoch, ulong nonce, IReadOnlyList<ItemWire> items) =>
        new EthereumMessageSigner().Sign(Blockchain.Contracts.AttestationDigest.Hash(contract, chainId, epoch, nodeKey.GetPublicAddress(), nonce, ToItems(items)), nodeKey);

    public static string Recover(string contract, ulong chainId, Request r)
    {
        try
        {
            return new EthereumMessageSigner().EcRecover(Blockchain.Contracts.AttestationDigest.Hash(contract, chainId, r.Epoch, r.Verifier, r.Nonce, ToItems(r.Items)), r.Signature);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return "";
        }
    }
}
