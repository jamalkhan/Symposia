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

    // QA T1.4: on a Node Host, the gossip endpoint admits a peer the host recognized and nobody else.
    [Fact]
    public void GossipAuthentication_UsesTheHostTable()
    {
        var keys = Path.Combine(_dir.Path, "keys");
        Directory.CreateDirectory(keys);
        NodeIdentity Key(string name)
        {
            var id = new NodeIdentity(Microsoft.Extensions.Options.Options.Create(new NodeNetworkOptions { NodeIdentityKeyPath = Path.Combine(keys, name) }));
            id.EnsureLoadedOrGenerated();
            return id;
        }

        var (self, known, stranger) = (Key("a"), Key("b"), Key("c"));
        var context = new HostContext(new HostPaths(_dir.Path), new SecretStore(new HostPaths(_dir.Path)), new LogBuffer());
        var table = new PeerTable();
        table.Replace([new PeerEntry("http://127.0.0.1:5221", known.NodeId, known.NodeId, PeerStatus.Recognized, PeerFailureReason.None, DateTimeOffset.UnixEpoch)]);
        context.HostPeers = table;
        var gossip = new Symposia.Network.Node.Telemetry.Committee.TelemetryGossipNode(
            new Symposia.Network.Node.Telemetry.InMemoryTelemetryGossip(31337, "0x5FbDB2315678afecb367f032d93F642f64180aa3"), new HostTelemetryPeers(context),
            new Symposia.Network.Node.Telemetry.Committee.NodeIdentityTelemetrySigner(self),
            new Symposia.Network.Node.Telemetry.TelemetryEip712(31337, "0x00000000000000000000000000000000000000aa"), TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new Symposia.Network.Node.Telemetry.Committee.TelemetryGossipOptions()), 31337);
        var body = "{}"u8.ToArray();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string Sign(NodeIdentity who) => Nethereum.Hex.HexConvertors.Extensions.HexByteConvertorExtensions.ToHex(
            who.Sign(Symposia.Network.Node.Telemetry.Committee.TelemetryGossipNode.RequestDigest(31337, now, body)), true);

        Assert.Null(gossip.Authenticate(known.NodeId, now.ToString(), Sign(known), body));
        Assert.Equal(401, gossip.Authenticate(stranger.NodeId, now.ToString(), Sign(stranger), body));
    }
}
