using Symposia.Network.Node;
using Symposia.Network.Node.Telemetry.Committee;

namespace Symposia.NodeHost.Components;

/// <summary>
/// Telemetry gossip peers for a hosted role (#207): the host's recognized peers, read from the
/// network component's table on every call; none while the network component is stopped.
/// </summary>
public sealed class HostTelemetryPeers(HostContext context) : ITelemetryPeers
{
    public IReadOnlyList<(string Address, string Endpoint)> Peers() =>
        context.HostPeers is { } table ? new PeerTableTelemetryPeers(table).Peers() : [];
}
