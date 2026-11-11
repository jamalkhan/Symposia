using System.Net;
using Microsoft.Extensions.Options;
using Symposia.Network.Node.Telemetry;
using Symposia.Network.Node.Telemetry.Committee;

namespace Symposia.Network.Node.Tests;

/// <summary>Issue #207: heartbeats must leave the node, and Diagnostics must say when they don't.</summary>
public sealed class TelemetryGossipReachTests : IDisposable
{
    private static readonly TelemetrySchedule Schedule = new(CommitteeHarness.Genesis, 86_400); // 1h checkpoint windows

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym207-" + Guid.NewGuid().ToString("N"));
    private readonly NodeIdentity _self;
    private readonly NodeIdentity _peer;
    private readonly ManualTime _time = new(DateTimeOffset.FromUnixTimeSeconds(CommitteeHarness.Genesis + 60));
    private readonly MutablePeers _peers = new();
    private readonly ScriptedNetwork _network = new();
    private readonly TelemetryDiagnostics _diagnostics = new();
    private readonly TelemetryGossipNode _gossip;

    public TelemetryGossipReachTests()
    {
        Directory.CreateDirectory(_dir);
        _self = TestSupport.NewIdentity(_dir);
        _peer = TestSupport.NewIdentity(_dir);
        _gossip = new TelemetryGossipNode(new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry), _peers,
            new NodeIdentityTelemetrySigner(_self), new TelemetryEip712(TestSupport.ChainId, "0x00000000000000000000000000000000000000aa"), _time,
            Options.Create(new TelemetryGossipOptions { MaxQueuedItems = 5 }), TestSupport.ChainId, _diagnostics, new TelemetryScheduleSource(Schedule));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WithNoPeers_HeartbeatsStayQueued_AndGoOutOnceAPeerIsRecognized()
    {
        await _gossip.PublishAsync(Beat(0));
        Assert.Equal(0, await FlushAsync());
        Assert.Equal(0, _gossip.Metrics.Dropped);
        Assert.Equal(0, _diagnostics.Gossip.RecognizedPeers);

        _peers.Add(_peer.NodeId); // recognized mid-epoch, no restart
        Assert.Equal(1, await FlushAsync());

        Assert.Equal(1, _network.Delivered);
        Assert.Equal(_time.GetUtcNow(), _diagnostics.Gossip.LastPushSucceeded);
    }

    [Fact]
    public async Task QueueWithNoPeersIsBounded_OldestDropped()
    {
        for (uint s = 0; s < 8; s++)
        {
            await _gossip.PublishAsync(Beat(s));
        }

        await FlushAsync();

        Assert.Equal(3, _gossip.Metrics.Dropped);
    }

    [Fact]
    public async Task OwnAddressIsNeverATarget()
    {
        _peers.Add(_self.NodeId);
        await _gossip.PublishAsync(Beat(0));

        Assert.Equal(0, await FlushAsync());
        Assert.Equal(0, _network.Attempts);
    }

    [Fact]
    public async Task PeerAnswering404_IsSkippedForAWhile_AndIsNotAFailure()
    {
        _peers.Add(_peer.NodeId);
        _network.Status = HttpStatusCode.NotFound;
        await _gossip.PublishAsync(Beat(0));
        await FlushAsync();

        Assert.Equal(0, _diagnostics.Gossip.FailedPushes);
        var attempts = _network.Attempts;
        await _gossip.PublishAsync(Beat(1));
        await FlushAsync();
        Assert.Equal(attempts, _network.Attempts); // backed off

        _time.Advance(TelemetryGossipNode.UnsupportedPeerBackoff + TimeSpan.FromSeconds(1));
        _network.Status = HttpStatusCode.Accepted;
        Assert.Equal(1, await FlushAsync());
    }

    [Fact]
    public async Task FailedAnd429Pushes_AreCounted()
    {
        _peers.Add(_peer.NodeId);
        _network.Status = HttpStatusCode.TooManyRequests;
        await _gossip.PublishAsync(Beat(0));
        await FlushAsync();

        Assert.Equal(1, _diagnostics.Gossip.FailedPushes);
        Assert.Null(_diagnostics.Gossip.LastPushSucceeded);
    }

    [Fact]
    public async Task Warning_OnlyAfterAnAcceptedBeat_WithOtherMembers_AndAFullWindowWithoutAPush()
    {
        await FlushAsync(); // gossip starts with no peers
        Assert.Null(_diagnostics.GossipWarning(_time.GetUtcNow().AddHours(2), otherMembers: 2)); // nothing accepted yet

        _diagnostics.Accepted(0, 0, _time.GetUtcNow());
        Assert.Null(_diagnostics.GossipWarning(_time.GetUtcNow().AddMinutes(30), otherMembers: 2)); // first window after boot
        Assert.Null(_diagnostics.GossipWarning(_time.GetUtcNow().AddHours(2), otherMembers: 0)); // alone in the registry
        Assert.Contains("No recognized telemetry gossip peers", _diagnostics.GossipWarning(_time.GetUtcNow().AddHours(2), otherMembers: 2));

        _peers.Add(_peer.NodeId);
        _network.Status = HttpStatusCode.InternalServerError;
        await _gossip.PublishAsync(Beat(0));
        await FlushAsync();
        Assert.Contains("1 failed", _diagnostics.GossipWarning(_time.GetUtcNow().AddHours(2), otherMembers: 2));

        _time.Advance(TimeSpan.FromHours(2));
        _network.Status = HttpStatusCode.Accepted;
        await _gossip.PublishAsync(Beat(1));
        await FlushAsync();
        Assert.Null(_diagnostics.GossipWarning(_time.GetUtcNow(), otherMembers: 2)); // clears after a successful push
    }

    // Review (Lenny 1/2, Frank 1): a 404 or 401 from every target must never lose the queue.
    [Fact]
    public async Task OnlyPeerAnswers404_NothingIsLost_AndItAllGoesOutToARealPeerLater()
    {
        var gossip = Node(new TelemetryGossipOptions { MaxItemsPerRequest = 2 });
        _peers.Add(_peer.NodeId);
        _network.Status = HttpStatusCode.NotFound;
        for (uint s = 0; s < 6; s++)
        {
            await gossip.PublishAsync(Beat(s));
        }

        await gossip.FlushAsync(new HttpClient(_network), default);
        Assert.Equal(1, _network.Attempts); // one 404, then it stops targeting that peer
        Assert.Equal(0, gossip.Metrics.Dropped);

        var real = TestSupport.NewIdentity(_dir);
        _peers.Add(real.NodeId);
        _network.Status = HttpStatusCode.Accepted;
        await gossip.FlushAsync(new HttpClient(_network), default);
        Assert.Equal(6, gossip.Metrics.Pushed);
        Assert.Equal(0, gossip.Metrics.Dropped);
    }

    [Fact]
    public async Task PeerThatHasntRecognizedUsYet_401_KeepsTheBatchQueued()
    {
        _peers.Add(_peer.NodeId);
        _network.Status = HttpStatusCode.Unauthorized;
        await _gossip.PublishAsync(Beat(0));
        await FlushAsync();
        Assert.Equal(1, _network.Attempts); // no pointless retries on a 401

        _network.Status = HttpStatusCode.Accepted;
        Assert.Equal(1, await FlushAsync());
        Assert.Equal(1, _gossip.Metrics.Pushed);
    }

    [Fact]
    public async Task PushedCountsEachItemOnce_WhateverTheFanOut()
    {
        _peers.Add(_peer.NodeId);
        _peers.Add(TestSupport.NewIdentity(_dir).NodeId);
        await _gossip.PublishAsync(Beat(0));

        Assert.Equal(2, await FlushAsync());
        Assert.Equal(1, _gossip.Metrics.Pushed);
    }

    [Fact]
    public async Task ComputeOnlyPeers_NeverRaiseTheWarning()
    {
        _peers.Add(_peer.NodeId);
        _network.Status = HttpStatusCode.NotFound;
        _diagnostics.Accepted(0, 0, _time.GetUtcNow());
        await _gossip.PublishAsync(Beat(0));
        await FlushAsync();
        await FlushAsync();

        Assert.Equal(1, _diagnostics.Gossip.UnsupportedPeers);
        Assert.Null(_diagnostics.GossipWarning(_time.GetUtcNow().AddHours(2), otherMembers: 1));
        Assert.NotNull(_diagnostics.GossipWarning(_time.GetUtcNow().AddHours(2), otherMembers: 2)); // a storage peer is still unreached
    }

    private TelemetryGossipNode Node(TelemetryGossipOptions options) =>
        new(new InMemoryTelemetryGossip(TestSupport.ChainId, TestSupport.Registry), _peers, new NodeIdentityTelemetrySigner(_self),
            new TelemetryEip712(TestSupport.ChainId, "0x00000000000000000000000000000000000000aa"), _time, Options.Create(options),
            TestSupport.ChainId, _diagnostics, new TelemetryScheduleSource(Schedule));

    private Task<int> FlushAsync() => _gossip.FlushAsync(new HttpClient(_network), default);

    private SignedTelemetry<HeartbeatSample> Beat(uint slot) =>
        _self.SignHeartbeat(new HeartbeatSample(_self.NodeId, 0, slot, Schedule.EpochStart(0) + slot * 300 + 7, 1_000, 800, 50_000, 40_000, 50, 120, 5_000, 1_000));

    private sealed class MutablePeers : ITelemetryPeers
    {
        private readonly List<(string, string)> _peers = [];

        public void Add(string address) => _peers.Add((address, $"http://{address.ToLowerInvariant()}/"));

        public IReadOnlyList<(string Address, string Endpoint)> Peers() => [.. _peers];
    }

    private sealed class ScriptedNetwork : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Accepted;

        public int Attempts { get; private set; }

        public int Delivered { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            if ((int)Status < 300)
            {
                Delivered++;
            }

            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }
}

/// <summary>Issue #207 FR1/FR3: a host-supplied peer view wins; a standalone node keeps its own peer table.</summary>
public sealed class TelemetryPeersWiringTests
{
    [Fact]
    public void HostSuppliedPeers_AreNotOverriddenByTheTelemetryNetwork()
    {
        var host = new NoPeers();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<ITelemetryPeers>(services, host);
        services.AddTelemetryNetwork(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build().GetSection("Telemetry"));

        Assert.Same(host, Assert.Single(services, d => d.ServiceType == typeof(ITelemetryPeers)).ImplementationInstance);
    }

    [Fact]
    public void StandaloneNode_UsesItsOwnPeerTable()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddTelemetryNetwork(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build().GetSection("Telemetry"));

        Assert.Equal(typeof(PeerTableTelemetryPeers), Assert.Single(services, d => d.ServiceType == typeof(ITelemetryPeers)).ImplementationType);
    }

    private sealed class NoPeers : ITelemetryPeers
    {
        public IReadOnlyList<(string Address, string Endpoint)> Peers() => [];
    }
}
