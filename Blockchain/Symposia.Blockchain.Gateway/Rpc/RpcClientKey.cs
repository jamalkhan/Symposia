using System.Net;
using System.Net.Sockets;

namespace Symposia.Blockchain.Gateway.Rpc;

/// <summary>
/// Which rate-limit bucket a proxy call belongs to (issue #182). The peer address, unless the peer
/// is a trusted proxy, in which case the right-most untrusted <c>X-Forwarded-For</c> entry. IPv6
/// clients are keyed by their /64, so one host can't rotate through its own range.
/// </summary>
internal static class RpcClientKey
{
    public const string Unknown = "unknown";

    public static string Resolve(IPAddress? peer, string? forwardedFor, IReadOnlyList<IPNetwork> trusted)
    {
        if (peer is null)
        {
            return Unknown;
        }

        peer = Normalize(peer);
        var client = peer;
        if (!string.IsNullOrWhiteSpace(forwardedFor) && IsTrusted(peer, trusted))
        {
            client = FromForwardedFor(forwardedFor, trusted) ?? peer;
        }

        return Bucket(client);
    }

    /// <summary>
    /// Walks from the right, stopping at the first hop that isn't a trusted proxy: everything left of
    /// it was written by the client and is never parsed. If every hop is trusted, the left-most.
    /// Null when the hop it stops at won't parse, so the caller falls back to the peer.
    /// </summary>
    private static IPAddress? FromForwardedFor(string header, IReadOnlyList<IPNetwork> trusted)
    {
        var hops = header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        IPAddress? last = null;
        for (var i = hops.Length - 1; i >= 0; i--)
        {
            if (!TryParseHop(hops[i], out var hop))
            {
                return null;
            }

            if (!IsTrusted(hop, trusted))
            {
                return hop;
            }

            last = hop;
        }

        return last;
    }

    /// <summary>Accepts <c>1.2.3.4</c>, <c>1.2.3.4:5678</c>, <c>2001:db8::1</c> and <c>[2001:db8::1]:5678</c>.</summary>
    private static bool TryParseHop(string text, out IPAddress address)
    {
        if (IPEndPoint.TryParse(text, out var endpoint))
        {
            address = Normalize(endpoint.Address);
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    public static bool TryParseNetwork(string text, out IPNetwork network)
    {
        if (text.Contains('/'))
        {
            return IPNetwork.TryParse(text.Trim(), out network);
        }

        if (IPAddress.TryParse(text.Trim(), out var address))
        {
            address = Normalize(address);
            network = new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        network = default;
        return false;
    }

    private static bool IsTrusted(IPAddress address, IReadOnlyList<IPNetwork> trusted) => trusted.Any(n => n.Contains(address));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string Bucket(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
