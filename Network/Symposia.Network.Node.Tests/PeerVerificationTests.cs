using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Symposia.Network.Node.Tests;

/// <summary>
/// Handshake verification and discovery (issue #129 FR-C*, FR-D*; QA TC-10–TC-14).
/// </summary>
public sealed class PeerVerificationTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "symposia-network-peers-" + Guid.NewGuid());
    private readonly FakeRegistry _registry = new();
    private readonly FakeHandshakeNetwork _network = new();
    private readonly NodeIdentity _self;
    private readonly NodeNetworkOptions _options;

    public PeerVerificationTests()
    {
        _self = TestSupport.NewIdentity(_dataDir);
        _options = TestSupport.Options(Path.Combine(_dataDir, "unused.key"));
        _options.AdvertisedEndpoint = "http://self:5180";
    }

    private PeerVerifier Verifier() =>
        new(_self, _network, _registry, Options.Create(_options), TimeProvider.System);

    private PeerDiscoveryService Discovery(PeerTable table) =>
        new(_self, _registry, Verifier(), table, Options.Create(_options), NullLogger<PeerDiscoveryService>.Instance);

    private NodeIdentity RegisteredPeer(string endpoint)
    {
        var peer = TestSupport.NewIdentity(_dataDir);
        _registry.Members[peer.NodeId] = endpoint;
        _network.Host(endpoint, peer);
        return peer;
    }

    // TC-10 / Gherkin "Two registered nodes recognize each other".
    [Fact]
    public async Task RegisteredPeer_SigningCorrectly_IsRecognized()
    {
        var peer = RegisteredPeer("http://b:5180");

        var entry = await Verifier().VerifyAsync("http://b:5180", peer.NodeId, CancellationToken.None);

        Assert.Equal(PeerStatus.Recognized, entry.Status);
        Assert.Equal(peer.NodeId, entry.Address, ignoreCase: true);
    }

    // TC-11 / Gherkin "Impersonation is rejected".
    [Fact]
    public async Task ImpostorAtRegisteredEndpoint_IsAddressMismatch()
    {
        var honest = RegisteredPeer("http://b:5180");
        var attacker = TestSupport.NewIdentity(_dataDir);
        _network.Host("http://b:5180", attacker);

        var entry = await Verifier().VerifyAsync("http://b:5180", honest.NodeId, CancellationToken.None);

        Assert.Equal(PeerStatus.Unrecognized, entry.Status);
        Assert.Equal(PeerFailureReason.AddressMismatch, entry.Reason);
    }

    // Claiming another node's address without its key fails signature recovery.
    [Fact]
    public async Task ResponderClaimingAnotherAddress_IsSignatureInvalid()
    {
        var honest = RegisteredPeer("http://b:5180");
        var attacker = TestSupport.NewIdentity(_dataDir);
        _network.Responders["http://b:5180"] = req => new HandshakeResponse(
            honest.NodeId, attacker.SignHandshake(req.Dialer, Convert.FromHexString(req.Nonce[2..])));

        var entry = await Verifier().VerifyAsync("http://b:5180", honest.NodeId, CancellationToken.None);

        Assert.Equal(PeerFailureReason.SignatureInvalid, entry.Reason);
    }

    // TC-12 / Gherkin "Unregistered node is rejected".
    [Fact]
    public async Task UnregisteredSeedPeer_IsNotRegistered()
    {
        var stranger = TestSupport.NewIdentity(_dataDir);
        _network.Host("http://c:5180", stranger);

        var entry = await Verifier().VerifyAsync("http://c:5180", expectedAddress: null, CancellationToken.None);

        Assert.Equal(PeerFailureReason.NotRegistered, entry.Reason);
    }

    // TC-13: a signature bound to a different nonce does not verify.
    [Fact]
    public async Task ReplayedSignatureForOldNonce_IsSignatureInvalid()
    {
        var peer = RegisteredPeer("http://b:5180");
        var staleNonce = new byte[PeerHandshake.NonceLength];
        _network.Responders["http://b:5180"] = req => new HandshakeResponse(
            peer.NodeId, peer.SignHandshake(req.Dialer, staleNonce));

        var entry = await Verifier().VerifyAsync("http://b:5180", peer.NodeId, CancellationToken.None);

        Assert.Equal(PeerFailureReason.SignatureInvalid, entry.Reason);
    }

    // TC-13: a signature made for a different dialer does not verify.
    [Fact]
    public async Task SignatureForDifferentDialer_IsSignatureInvalid()
    {
        var peer = RegisteredPeer("http://b:5180");
        var otherDialer = TestSupport.NewIdentity(_dataDir).NodeId;
        _network.Responders["http://b:5180"] = req => new HandshakeResponse(
            peer.NodeId, peer.SignHandshake(otherDialer, Convert.FromHexString(req.Nonce[2..])));

        var entry = await Verifier().VerifyAsync("http://b:5180", peer.NodeId, CancellationToken.None);

        Assert.Equal(PeerFailureReason.SignatureInvalid, entry.Reason);
    }

    [Fact]
    public async Task UnreachableEndpoint_IsUnreachable()
    {
        var entry = await Verifier().VerifyAsync("http://nowhere:5180", null, CancellationToken.None);

        Assert.Equal(PeerFailureReason.Unreachable, entry.Reason);
    }

    // FR-C1/FR-C2: discovery handshakes registry members and seeds, excluding itself.
    [Fact]
    public async Task DiscoveryCycle_RecognizesRegistryPeers_AndSkipsSelf()
    {
        _registry.Members[_self.NodeId] = _options.AdvertisedEndpoint;
        var b = RegisteredPeer("http://b:5180");
        var stranger = TestSupport.NewIdentity(_dataDir);
        _network.Host("http://seed:5180", stranger);
        _options.SeedPeers = ["http://seed:5180", "http://self:5180"];
        var table = new PeerTable();

        await Discovery(table).RunCycleAsync(CancellationToken.None);

        var peers = table.Snapshot();
        Assert.Equal(2, peers.Count);
        Assert.Contains(peers, p => p.Endpoint == "http://b:5180" && p.Status == PeerStatus.Recognized);
        Assert.Contains(peers, p => p.Endpoint == "http://seed:5180" && p.Reason == PeerFailureReason.NotRegistered);
        Assert.DoesNotContain(peers, p => p.Endpoint == "http://self:5180");
    }

    // TC-14: gateway down keeps the last known table.
    [Fact]
    public async Task DiscoveryCycle_GatewayDown_KeepsLastPeerTable()
    {
        RegisteredPeer("http://b:5180");
        var table = new PeerTable();
        var discovery = Discovery(table);
        await discovery.RunCycleAsync(CancellationToken.None);

        _registry.Down = true;
        await discovery.RunCycleAsync(CancellationToken.None);

        Assert.Single(table.Recognized());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }
}
