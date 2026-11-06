using System.Net;
using System.Net.Sockets;

namespace Symposia.Network.Node;

/// <summary>
/// Canonical form and dial policy for node endpoints (issue #129). Endpoints
/// are normalized before signing and before every comparison, so cosmetic
/// config differences (case, trailing slash) never trigger a re-registration
/// or split one peer into two. Because any registered key can publish any
/// endpoint, nodes refuse to dial anything but plain http(s) to non-link-local
/// hosts — otherwise one registration fans out requests to e.g. cloud
/// metadata services from every node in the network.
/// </summary>
public static class NodeEndpoint
{
    /// <summary>Lowercased scheme/host, explicit port, no path/trailing slash. Null if not an absolute http(s) URL.</summary>
    public static string? Normalize(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}:{uri.Port}";
    }

    /// <summary>Whether this node may dial <paramref name="endpoint"/>: canonical http(s) and not link-local.</summary>
    public static bool IsDialable(string endpoint)
    {
        var normalized = Normalize(endpoint);
        if (normalized is null)
        {
            return false;
        }

        var host = new Uri(normalized).Host.Trim('[', ']');
        return !IPAddress.TryParse(host, out var ip) || !IsLinkLocal(ip);
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 169 && b[1] == 254;
        }

        return ip.IsIPv6LinkLocal || ip.Equals(IPAddress.Parse("fd00:ec2::254"));
    }
}
