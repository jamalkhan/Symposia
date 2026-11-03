using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Symposia.NodeHost.Settings;

/// <summary>
/// This machine's private LAN IPv4 address, offered in Settings as the advertised endpoint so
/// a node on a multi-machine network doesn't register 127.0.0.1 (issue #135 FR-E1).
/// </summary>
public static class LanAddress
{
    public static string? Detect()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderBy(n => n.GetIPProperties().GatewayAddresses.Count == 0) // interfaces with a default route first
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
                ?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
