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

    /// <summary>The right-most entry that isn't a trusted proxy; the left-most if all are; null if any entry is malformed.</summary>
    private static IPAddress? FromForwardedFor(string header, IReadOnlyList<IPNetwork> trusted)
    {
        var hops = new List<IPAddress>();
        foreach (var entry in header.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(entry, out var hop))
            {
                return null;
            }

            hops.Add(Normalize(hop));
        }

        for (var i = hops.Count - 1; i >= 0; i--)
        {
            if (!IsTrusted(hops[i], trusted))
            {
                return hops[i];
            }
        }

        return hops.Count > 0 ? hops[0] : null;
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
