using System.Collections.Concurrent;
using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node;

/// <summary>A signed storage RPC credential, carried in gRPC metadata (<see cref="StorageRpcAuth"/>).</summary>
public sealed record StorageRpcToken(string Caller, long UnixSeconds, string Signature);

/// <summary>
/// Authenticates calls to a storage node's gRPC API (issue #140). The caller signs, with its node key
/// (EIP-191, the same primitive as the #129 handshake), a digest binding the network (chain and
/// registry), the callee (audience), the caller, the gRPC method and a timestamp. The callee accepts
/// the call only if the signature recovers to the claimed caller, the token is fresh and addressed to
/// it on its own network, and the caller is registered in that network's on-chain NodeRegistry.
/// No secret ever crosses the wire. Replay within the freshness window against the same node and
/// method is possible on a cleartext link; TLS between gateway and nodes is the follow-up that closes it.
/// </summary>
public static class StorageRpcAuth
{
    public const string Domain = "Symposia.StorageRpc.v1";
    public const string CallerHeader = "x-symposia-caller";
    public const string TimestampHeader = "x-symposia-ts";
    public const string SignatureHeader = "x-symposia-sig";
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(60);

    private static readonly EthereumMessageSigner MessageSigner = new();

    public static byte[] Digest(ulong chainId, string registryAddress, string audience, string caller, string method, long unixSeconds) =>
        Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncodedPacked(
            new ABIValue("string", Domain),
            new ABIValue("uint256", chainId),
            new ABIValue("address", registryAddress),
            new ABIValue("address", audience),
            new ABIValue("address", caller),
            new ABIValue("string", method),
            new ABIValue("uint64", (ulong)unixSeconds)));

    public static StorageRpcToken Sign(EthECKey key, ulong chainId, string registryAddress, string audience, string method, long unixSeconds)
    {
        var caller = key.GetPublicAddress();
        return new StorageRpcToken(caller, unixSeconds,
            MessageSigner.Sign(Digest(chainId, registryAddress, audience, caller, method, unixSeconds), key));
    }

    /// <summary>
    /// Checks everything except registry membership. Returns null when the token is valid for
    /// <paramref name="self"/>, otherwise a short reason for logs and the Unauthenticated status.
    /// </summary>
    public static string? Validate(
        StorageRpcToken? token, ulong chainId, string registryAddress, string self, string method, DateTimeOffset now)
    {
        if (token is null || string.IsNullOrEmpty(token.Caller) || string.IsNullOrEmpty(token.Signature))
        {
            return "missing credentials";
        }

        if (!AddressUtil.Current.IsValidEthereumAddressHexFormat(token.Caller))
        {
            return "caller is not an address";
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - token.UnixSeconds) > MaxSkew.TotalSeconds)
        {
            return "stale or future timestamp";
        }

        string? signer;
        try
        {
            // The digest commits to this node (audience), its network and the method, so a token
            // minted for another node, network or method recovers to some other address.
            signer = MessageSigner.EcRecover(Digest(chainId, registryAddress, self, token.Caller, method, token.UnixSeconds), token.Signature);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return "malformed signature";
        }

        return string.Equals(signer, token.Caller, StringComparison.OrdinalIgnoreCase)
            ? null
            : "signature does not match caller, audience, network or method";
    }
}

/// <summary>
/// Whether a caller is a registered member of this node's network, cached for <see cref="Ttl"/>
/// (positive and negative) so storage RPCs don't hit the chain gateway per call. If the registry
/// can't be reached and there's no cached answer, the caller is refused.
/// </summary>
public sealed class StorageRpcCallerRegistry(INodeRegistryClient registry, TimeProvider? time = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (bool Registered, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsRegisteredAsync(string caller, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(caller, out var hit) && now - hit.At < Ttl)
        {
            return hit.Registered;
        }

        try
        {
            var status = await registry.GetStatusAsync(caller, ct);
            _cache[caller] = (status.Registered, now);
            return status.Registered;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Unreachable registry: an expired cached "yes" is still better evidence than nothing for a
            // short while, but with no answer at all the safe default is to refuse.
            return hit.At != default && hit.Registered && now - hit.At < Ttl * 5;
        }
    }
}
