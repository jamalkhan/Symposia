using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Threading.RateLimiting;

namespace Symposia.Blockchain.Gateway.Rpc;

/// <summary>
/// Abuse controls for <c>/v1/rpc</c> (issue #182): a token bucket per client and a gateway-wide
/// concurrency cap, neither of which queues. Called from the handler rather than as middleware,
/// because a call's price depends on its parsed method. Tokens are never refunded, so probing
/// with refused calls isn't free.
///
/// Buckets are kept here rather than in a <c>PartitionedRateLimiter</c> so eviction is countable:
/// one shared timer refills the buckets and drops those that are full and idle, which
/// never hands a client a refill it hadn't already earned.
/// </summary>
public sealed class RpcProxyLimiter : IDisposable
{
    public const string MeterName = "Symposia.Blockchain.Gateway.RpcProxy";
    private const int MaxLogThrottleEntries = 10_000;
    private static readonly TimeSpan ReplenishTick = TimeSpan.FromMilliseconds(100);

    private readonly RpcRateLimitOptions _options;
    private readonly IReadOnlyList<IPNetwork> _trusted;
    private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _buckets = new();
    private readonly ConcurrencyLimiter? _inFlight;
    private readonly Timer? _replenisher;
    private readonly ConcurrentDictionary<string, long> _lastLoggedMinute = new();
    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Counter<long> _forwarded;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _upstreamErrors;

    public RpcProxyLimiter(RpcRateLimitOptions options, IMeterFactory? meters = null)
    {
        _options = options;
        _ownsMeter = meters is null;
        _meter = meters?.Create(MeterName) ?? new Meter(MeterName);
        _forwarded = _meter.CreateCounter<long>("rpc_proxy.forwarded");
        _rejected = _meter.CreateCounter<long>("rpc_proxy.rejected");
        _upstreamErrors = _meter.CreateCounter<long>("rpc_proxy.upstream_errors");
        if (!options.Enabled)
        {
            _trusted = [];
            return;
        }

        _trusted = [.. options.TrustedProxies.Select(p => RpcClientKey.TryParseNetwork(p, out var n)
            ? n : throw new InvalidOperationException($"Gateway:RpcProxy:RateLimit:TrustedProxies has an invalid entry: '{p}'."))];
        _inFlight = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = options.MaxConcurrentRequests, QueueLimit = 0 });
        // Ticks well inside the 1 s refill period: a bucket only refills once a whole period has passed
        // since its last refill, so a 1 s tick would land just short every other second and halve the rate.
        _replenisher = new Timer(_ => Replenish(), null, ReplenishTick, ReplenishTick);
    }

    /// <summary>Clients currently holding a bucket; for tests and diagnostics.</summary>
    internal int BucketCount => _buckets.Count;

    public string ClientOf(HttpContext http) =>
        RpcClientKey.Resolve(http.Connection.RemoteIpAddress, http.Request.Headers["X-Forwarded-For"].ToString(), _trusted);

    /// <summary>
    /// Takes <paramref name="tokens"/> from the client's bucket. When it's short, returns the
    /// Retry-After seconds needed to earn <paramref name="callCost"/> tokens (the whole call's price).
    /// </summary>
    public int? TryTake(string client, int tokens, int callCost = 1)
    {
        if (!_options.Enabled || tokens <= 0)
        {
            return null;
        }

        var bucket = _buckets.GetOrAdd(client, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = _options.BurstTokens,
            TokensPerPeriod = _options.TokensPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = false,
        }));
        using var lease = bucket.AttemptAcquire(tokens);
        if (lease.IsAcquired)
        {
            return null;
        }

        var available = Math.Max(0, bucket.GetStatistics()?.CurrentAvailablePermits ?? 0);
        var needed = Math.Max(callCost, tokens - available);
        return Math.Max(1, (int)Math.Ceiling((double)needed / _options.TokensPerSecond));
    }

    /// <summary>Takes the rest of an <c>eth_getLogs</c> call's price, after the one token every call pays.</summary>
    public int? TryTakeGetLogs(string client) => TryTake(client, _options.GetLogsCost - 1, _options.GetLogsCost);

    /// <summary>A slot to dispose when the call finishes, or null when the gateway is at its concurrency cap.</summary>
    public IDisposable? TryEnter()
    {
        if (_inFlight is null)
        {
            return NoLease.Instance;
        }

        var lease = _inFlight.AttemptAcquire();
        if (lease.IsAcquired)
        {
            return lease;
        }

        lease.Dispose();
        return null;
    }

    public void CountForwarded() => _forwarded.Add(1);

    /// <summary>Calls refused by the proxy: <c>rate_limited</c>, <c>concurrency</c>, <c>method</c> or <c>invalid</c>.</summary>
    public void CountRejected(string reason) => _rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Upstream failures, kept apart from rejections so a provider outage doesn't look like abuse.</summary>
    public void CountUpstreamError() => _upstreamErrors.Add(1);

    /// <summary>
    /// True at most once per client per minute, so a flood can't also flood the logs. Past
    /// <see cref="MaxLogThrottleEntries"/> clients in one minute, new ones aren't logged at all.
    /// </summary>
    public bool ShouldLog(string client)
    {
        var minute = Environment.TickCount64 / 60_000;
        if (_lastLoggedMinute.Count >= MaxLogThrottleEntries)
        {
            foreach (var (key, logged) in _lastLoggedMinute)
            {
                if (logged < minute)
                {
                    _lastLoggedMinute.TryRemove(key, out _);
                }
            }

            if (_lastLoggedMinute.Count >= MaxLogThrottleEntries && !_lastLoggedMinute.ContainsKey(client))
            {
                return false;
            }
        }

        var previous = _lastLoggedMinute.GetOrAdd(client, -1);
        return previous != minute && _lastLoggedMinute.TryUpdate(client, minute, previous);
    }

    /// <summary>One tick of the shared timer: refill every bucket, then drop the full, idle ones.</summary>
    internal void Replenish()
    {
        foreach (var (client, bucket) in _buckets)
        {
            bucket.TryReplenish();
            if (bucket.IdleDuration is not null && bucket.GetStatistics()?.CurrentAvailablePermits >= _options.BurstTokens
                && _buckets.TryRemove(new KeyValuePair<string, TokenBucketRateLimiter>(client, bucket)))
            {
                // Not disposed: a call that fetched it just before removal may still acquire from it.
                // Without auto-replenishment it holds no timer, so the GC reclaims it.
            }
        }
    }

    public void Dispose()
    {
        _replenisher?.Dispose();
        _inFlight?.Dispose();
        foreach (var bucket in _buckets.Values)
        {
            bucket.Dispose();
        }

        if (_ownsMeter)
        {
            _meter.Dispose(); // a factory's meters are the factory's to dispose
        }
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }
}
