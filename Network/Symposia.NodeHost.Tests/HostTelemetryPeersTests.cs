using Symposia.Network.Node;
using Symposia.NodeHost.Components;
using Symposia.NodeHost.Diagnostics;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Tests;

/// <summary>Issue #207: hosted roles gossip to the host's recognized peers, following the network component.</summary>
public sealed class HostTelemetryPeersTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void FollowsTheHostPeerTable_RecognizedOnly_AndEmptyWhileTheNetworkIsStopped()
    {
        var context = new HostContext(new HostPaths(_dir.Path), new SecretStore(new HostPaths(_dir.Path)), new LogBuffer());
        var peers = new HostTelemetryPeers(context);
        Assert.Empty(peers.Peers());

        var table = new PeerTable();
        context.HostPeers = table;
        table.Replace([
            new PeerEntry("http://127.0.0.1:5221", "0xB", "0xB", PeerStatus.Recognized, PeerFailureReason.None, DateTimeOffset.UnixEpoch),
            new PeerEntry("http://127.0.0.1:5222", "0xC", "0xC", PeerStatus.Unrecognized, PeerFailureReason.Unreachable, DateTimeOffset.UnixEpoch)]);
        Assert.Equal([("0xB", "http://127.0.0.1:5221")], peers.Peers()); // read per call: a peer recognized later shows up

        context.HostPeers = null;
        Assert.Empty(peers.Peers());
    }
}
