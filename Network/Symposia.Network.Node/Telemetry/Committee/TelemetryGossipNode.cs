using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.ABI;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;

namespace Symposia.Network.Node.Telemetry.Committee;

/// <summary>This node's address and raw-digest signer (r‖s‖v), e.g. its <see cref="NodeIdentity"/>.</summary>
public interface ITelemetrySigner
{
    string Address { get; }

    byte[] Sign(byte[] digest);
}

public sealed class NodeIdentityTelemetrySigner(NodeIdentity identity) : ITelemetrySigner
{
    public string Address => identity.NodeId;

    public byte[] Sign(byte[] digest) => identity.Sign(digest);
}

/// <summary>Peers to gossip to: address and base URL.</summary>
public interface ITelemetryPeers
{
    IReadOnlyList<(string Address, string Endpoint)> Peers();
}

/// <summary>Recognized peers from #129 discovery, i.e. registry-verified nodes.</summary>
public sealed class PeerTableTelemetryPeers(PeerTable table) : ITelemetryPeers
{
    public IReadOnlyList<(string Address, string Endpoint)> Peers() =>
        [.. table.Recognized().Where(p => p.Address is not null).Select(p => (p.Address!, p.Endpoint))];
}

public sealed class TelemetryGossipOptions
{
    public int FanOut { get; set; } = 8;

    public int FlushIntervalMilliseconds { get; set; } = 2000;

    public int MaxItemsPerRequest { get; set; } = 256;

    public int MaxBodyBytes { get; set; } = 1 << 20;

    public int MaxAttemptsPerPeer { get; set; } = 3;

    /// <summary>Requests accepted per sender per 10 seconds.</summary>
    public int RequestsPerSenderPer10Seconds { get; set; } = 40;

    public long MaxClockSkewSeconds { get; set; } = 60;

    /// <summary>Items kept queued while no peer is reachable (#207); the oldest are dropped beyond this.</summary>
    public int MaxQueuedItems { get; set; } = 10_000;
}

/// <summary>
/// HTTP gossip for telemetry (issue #158 FR1), built on the #75 in-memory store, which keeps
/// owning admission, de-duplication and equivocation handling. Anything stored for the first time
/// (local or received) is queued and pushed in batches to <see cref="TelemetryGossipOptions.FanOut"/>
/// random recognized peers, which forward it once in turn, so it floods in O(log N) hops without
/// loops. Committee endorsements and records signatures ride the same envelopes.
///
/// Requests are authenticated per request rather than by session: the sender signs
/// <c>keccak("Symposia.TelemetryGossip.v1" ‖ chainId ‖ sentAt ‖ keccak(body))</c>, and receivers
/// accept only recognized peers, within a clock-skew bound and a per-sender rate limit.
/// </summary>
public sealed class TelemetryGossipNode : ITelemetryGossip
{
    public const string Path = "/v1/telemetry/gossip";
    public const string SenderHeader = "X-Symposia-Sender";
    public const string SentAtHeader = "X-Symposia-Sent-At";
    public const string SignatureHeader = "X-Symposia-Signature";
    private const string Domain = "Symposia.TelemetryGossip.v1";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InMemoryTelemetryGossip _store;
    private readonly ITelemetryPeers _peers;
    private readonly ITelemetrySigner _signer;
    private readonly TelemetryEip712 _eip712;
    private readonly TimeProvider _time;
    private readonly TelemetryGossipOptions _options;
    private readonly ulong _chainId;
    private readonly ConcurrentQueue<object> _outbound = new();
    private readonly ConcurrentDictionary<string, (long Window, int Count)> _rate = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unsupported = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly TelemetryDiagnostics? _diagnostics;
    private readonly TelemetryScheduleSource? _schedule;
    private readonly ILogger? _logger;

    /// <summary>How long a peer that answers 404 (it hosts no gossip, e.g. compute-only) is skipped.</summary>
    public static readonly TimeSpan UnsupportedPeerBackoff = TimeSpan.FromMinutes(10);

    public TelemetryGossipNode(InMemoryTelemetryGossip store, ITelemetryPeers peers, ITelemetrySigner signer, TelemetryEip712 eip712,
        TimeProvider time, IOptions<TelemetryGossipOptions> options, ulong chainId, TelemetryDiagnostics? diagnostics = null,
        TelemetryScheduleSource? schedule = null, ILogger<TelemetryGossipNode>? logger = null)
    {
        _diagnostics = diagnostics;
        _schedule = schedule;
        _logger = logger;
        _store = store;
        _peers = peers;
        _signer = signer;
        _eip712 = eip712;
        _time = time;
        _options = options.Value;
        _chainId = chainId;
        _store.HeartbeatReceived += s => _outbound.Enqueue(s);
        _store.AttestationReceived += a => _outbound.Enqueue(a);
    }

    public CommitteeMessageStore Committee { get; } = new();

    public TelemetryGossipMetrics Metrics { get; } = new();

    public InMemoryTelemetryGossip Store => _store;

    public event Action<SignedTelemetry<HeartbeatSample>>? HeartbeatReceived
    {
        add => _store.HeartbeatReceived += value;
        remove => _store.HeartbeatReceived -= value;
    }

    public event Action<SignedTelemetry<Attestation>>? AttestationReceived
    {
        add => _store.AttestationReceived += value;
        remove => _store.AttestationReceived -= value;
    }

    public async ValueTask<TelemetryPublishResult> PublishAsync(SignedTelemetry<HeartbeatSample> sample, CancellationToken ct = default)
    {
        var stored = await _store.PublishAsync(sample, ct);
        Count(stored, Metrics.Emitted, Metrics.EmittedRejected);
        return stored;
    }

    public async ValueTask<TelemetryPublishResult> PublishAsync(SignedTelemetry<Attestation> attestation, CancellationToken ct = default)
    {
        // Emitted has only ever counted heartbeats; attestations feed the per-reason rejections only.
        var stored = await _store.PublishAsync(attestation, ct);
        Count(stored, null, Metrics.EmittedRejected);
        return stored;
    }

    public bool Publish(CheckpointEndorsement endorsement)
    {
        if (!endorsement.IsValid(_eip712) || !Committee.Add(endorsement))
        {
            return false;
        }

        _outbound.Enqueue(endorsement);
        return true;
    }

    public bool Publish(RecordsSignature signature)
    {
        if (!signature.IsValid() || !Committee.Add(signature))
        {
            return false;
        }

        _outbound.Enqueue(signature);
        return true;
    }

    /// <summary>Stores every valid item of a received envelope; new ones are forwarded on the next flush.</summary>
    public async Task ReceiveAsync(TelemetryEnvelope envelope, CancellationToken ct)
    {
        foreach (var h in envelope.Heartbeats ?? [])
        {
            Count(await _store.PublishAsync(h, ct), Metrics.Received, Metrics.ReceivedRejected);
        }

        foreach (var a in envelope.Attestations ?? [])
        {
            Count(await _store.PublishAsync(a, ct), Metrics.Received, Metrics.ReceivedRejected);
        }

        foreach (var e in envelope.Endorsements ?? [])
        {
            Publish(e);
        }

        foreach (var r in envelope.RecordSignatures ?? [])
        {
            Publish(r);
        }
    }

    /// <summary>Drains the outbound queue and pushes it to a random subset of peers. Returns envelopes sent.</summary>
    public async Task<int> FlushAsync(HttpClient http, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var peers = _peers.Peers()
            .Where(p => !string.Equals(p.Address, _signer.Address, StringComparison.OrdinalIgnoreCase)
                        && !(_unsupported.TryGetValue(p.Address, out var until) && until > now))
            .ToList();
        _diagnostics?.GossipTargets(peers.Count, WindowSeconds, now);
        if (peers.Count == 0)
        {
            // Keep the queue (#207): heartbeats from before the first discovery tick, or during a
            // network restart, must still reach the committee once a peer is recognized.
            var excess = _outbound.Count - _options.MaxQueuedItems;
            for (; excess > 0 && _outbound.TryDequeue(out _); excess--)
            {
                Interlocked.Increment(ref Metrics.DroppedField);
            }

            if (!_outbound.IsEmpty)
            {
                WarnOnce("", "No recognized telemetry gossip peers; {Count} items stay queued until one is recognized.", _outbound.Count);
            }

            return 0;
        }

        var sent = 0;
        while (!_outbound.IsEmpty)
        {
            var batch = new List<object>(_options.MaxItemsPerRequest);
            while (batch.Count < _options.MaxItemsPerRequest && _outbound.TryDequeue(out var item))
            {
                batch.Add(item);
            }

            var envelope = new TelemetryEnvelope(
                [.. batch.OfType<SignedTelemetry<HeartbeatSample>>()],
                [.. batch.OfType<SignedTelemetry<Attestation>>()],
                [.. batch.OfType<CheckpointEndorsement>()],
                [.. batch.OfType<RecordsSignature>()]);
            var targets = peers.OrderBy(_ => Random.Shared.Next()).Take(_options.FanOut);
            foreach (var peer in targets)
            {
                if (await PushAsync(http, peer, envelope, ct))
                {
                    sent++;
                }
            }
        }

        return sent;
    }

    /// <summary>Pushes an envelope straight to specific peers (e.g. missing samples to the committee, #158 huddle item 12).</summary>
    public async Task PushToAsync(HttpClient http, IEnumerable<string> addresses, TelemetryEnvelope envelope, CancellationToken ct)
    {
        var wanted = addresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in _peers.Peers().Where(p => wanted.Contains(p.Address)))
        {
            await PushAsync(http, peer, envelope, ct);
        }
    }

    /// <summary>The checkpoint window the warnings and diagnostics are measured over.</summary>
    private long WindowSeconds => _schedule?.Current is { } s && s.CheckpointsPerEpoch > 0
        ? Math.Max(1, s.EpochSeconds / s.CheckpointsPerEpoch)
        : 3600;

    private async Task<bool> PushAsync(HttpClient http, (string Address, string Endpoint) peer, TelemetryEnvelope envelope, CancellationToken ct)
    {
        switch (await SendAsync(http, peer.Endpoint, envelope, ct))
        {
            case PushOutcome.Delivered:
                _diagnostics?.GossipPushed(_time.GetUtcNow());
                return true;
            case PushOutcome.Unsupported:
                // Not a failure: this peer hosts no gossip (e.g. a compute-only host).
                _unsupported[peer.Address] = _time.GetUtcNow() + UnsupportedPeerBackoff;
                return false;
            default:
                _diagnostics?.GossipPushFailed();
                WarnOnce(peer.Address, "Telemetry gossip push to {Peer} at {Endpoint} failed; its {Count} items were dropped.",
                    peer.Address, peer.Endpoint, envelope.Count);
                return false;
        }
    }

    /// <summary>Logs at most once per key per checkpoint window (#207 FR4).</summary>
    private void WarnOnce(string key, string message, params object[] args)
    {
        if (_logger is null)
        {
            return;
        }

        var window = _time.GetUtcNow().ToUnixTimeSeconds() / WindowSeconds;
        if (_warned.TryGetValue(key, out var last) && last == window)
        {
            return;
        }

        _warned[key] = window;
#pragma warning disable CA2254 // the templates above are constants
        _logger.LogWarning(message, args);
#pragma warning restore CA2254
    }

    private enum PushOutcome
    {
        Delivered,
        Unsupported,
        Failed,
    }

    private async Task<PushOutcome> SendAsync(HttpClient http, string endpoint, TelemetryEnvelope envelope, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        for (var attempt = 1; attempt <= _options.MaxAttemptsPerPeer; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(endpoint), Path))
                {
                    Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
                };
                var sentAt = _time.GetUtcNow().ToUnixTimeSeconds();
                request.Headers.Add(SenderHeader, _signer.Address);
                request.Headers.Add(SentAtHeader, sentAt.ToString());
                request.Headers.Add(SignatureHeader, _signer.Sign(RequestDigest(_chainId, sentAt, body)).ToHex(true));
                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Add(ref Metrics.PushedField, envelope.Count);
                    return PushOutcome.Delivered;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Undelivered, so the items count as dropped, but the peer simply doesn't gossip.
                    Interlocked.Add(ref Metrics.DroppedField, envelope.Count);
                    return PushOutcome.Unsupported;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
            }
        }

        Interlocked.Add(ref Metrics.DroppedField, envelope.Count);
        return PushOutcome.Failed;
    }

    public static byte[] RequestDigest(ulong chainId, long sentAt, byte[] body) =>
        Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncodedPacked(
            new ABIValue("string", Domain), new ABIValue("uint256", chainId), new ABIValue("uint64", (ulong)sentAt),
            new ABIValue("bytes32", Sha3Keccack.Current.CalculateHash(body))));

    /// <summary>Validates an inbound request's sender, freshness, signature and rate; null if accepted, else the HTTP status to return.</summary>
    internal int? Authenticate(string? sender, string? sentAtText, string? signature, byte[] body)
    {
        if (sender is null || signature is null || !long.TryParse(sentAtText, out var sentAt))
        {
            return StatusCodes.Status401Unauthorized;
        }

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - sentAt) > _options.MaxClockSkewSeconds
            || !string.Equals(TelemetryEip712.Recover(RequestDigest(_chainId, sentAt, body), signature), sender, StringComparison.OrdinalIgnoreCase)
            || !_peers.Peers().Any(p => string.Equals(p.Address, sender, StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCodes.Status401Unauthorized;
        }

        var window = now / 10;
        var rate = _rate.AddOrUpdate(sender, _ => (window, 1), (_, r) => r.Window == window ? (window, r.Count + 1) : (window, 1));
        return rate.Count > _options.RequestsPerSenderPer10Seconds ? StatusCodes.Status429TooManyRequests : null;
    }

    internal int MaxBodyBytes => _options.MaxBodyBytes;

    internal int MaxItemsPerRequest => _options.MaxItemsPerRequest;

    private static void Count(TelemetryPublishResult result, TelemetryCounter? accepted, TelemetryRejectionCounters rejected)
    {
        if (result.Accepted)
        {
            accepted?.Increment();
        }
        else
        {
            rejected[result.Rejection].Increment();
        }
    }
}

public sealed class TelemetryCounter
{
    private long _value;

    public long Value => Interlocked.Read(ref _value);

    public void Increment() => Interlocked.Increment(ref _value);
}

/// <summary>One <see cref="TelemetryCounter"/> per <see cref="TelemetryRejection"/> (#183).</summary>
public sealed class TelemetryRejectionCounters
{
    private readonly TelemetryCounter[] _counters = [.. Enum.GetValues<TelemetryRejection>().Select(_ => new TelemetryCounter())];

    public TelemetryCounter this[TelemetryRejection rejection] => _counters[(int)rejection];
}

/// <summary>Gossip counters (#158 FR6).</summary>
public sealed class TelemetryGossipMetrics
{
    internal long PushedField;
    internal long DroppedField;

    public TelemetryCounter Emitted { get; } = new();

    public TelemetryCounter Received { get; } = new();

    /// <summary>Locally published items refused, by reason (#183).</summary>
    public TelemetryRejectionCounters EmittedRejected { get; } = new();

    /// <summary>Items received from peers and refused, by reason (#183); e.g. a peer with a skewed clock.</summary>
    public TelemetryRejectionCounters ReceivedRejected { get; } = new();

    public long Pushed => Interlocked.Read(ref PushedField);

    public long Dropped => Interlocked.Read(ref DroppedField);
}

/// <summary>Flushes the outbound gossip queue on a short timer.</summary>
public sealed class TelemetryGossipFlusher(TelemetryGossipNode node, IHttpClientFactory http, IOptions<TelemetryGossipOptions> options,
    ILogger<TelemetryGossipFlusher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, options.Value.FlushIntervalMilliseconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await node.FlushAsync(http.CreateClient(nameof(TelemetryGossipNode)), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Telemetry gossip flush failed; items stay queued for the next tick.");
            }
        }
    }
}

public static class TelemetryGossipEndpoints
{
    /// <summary>Maps <c>POST /v1/telemetry/gossip</c> (#158 FR1).</summary>
    public static IEndpointRouteBuilder MapTelemetryGossip(this IEndpointRouteBuilder app)
    {
        app.MapPost(TelemetryGossipNode.Path, async (HttpContext context, TelemetryGossipNode node, CancellationToken ct) =>
        {
            if (context.Request.ContentLength > node.MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > node.MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var body = buffer.ToArray();
            var headers = context.Request.Headers;
            var rejected = node.Authenticate(headers[TelemetryGossipNode.SenderHeader], headers[TelemetryGossipNode.SentAtHeader],
                headers[TelemetryGossipNode.SignatureHeader], body);
            if (rejected is not null)
            {
                return Results.StatusCode(rejected.Value);
            }

            TelemetryEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(body, TelemetryGossipNode.Json);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            if (envelope is null || envelope.Count > node.MaxItemsPerRequest)
            {
                return Results.BadRequest();
            }

            await node.ReceiveAsync(envelope, ct);
            return Results.Accepted();
        });
        return app;
    }
}
