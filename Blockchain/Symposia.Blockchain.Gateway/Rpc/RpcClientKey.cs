using System.Net;
using System.Net.Sockets;
using Symposia.Network.Node;

namespace Symposia.Blockchain.Gateway.Rpc;

/// <summary>
/// Which rate-limit bucket a proxy call belongs to (issue #182): the request's client
/// (<see cref="ClientAddress"/>, honoring trusted proxies). IPv6 clients are keyed by their /64, so
/// one host can't rotate through its own range.
/// </summary>
internal static class RpcClientKey
{
    public const string Unknown = "unknown";

    public static string Resolve(IPAddress? peer, string? forwardedFor, IReadOnlyList<IPNetwork> trusted) =>
        ClientAddress.Resolve(peer, forwardedFor, trusted) is { } client ? Bucket(client) : Unknown;

    public static bool TryParseNetwork(string text, out IPNetwork network) => ClientAddress.TryParseNetwork(text, out network);

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
