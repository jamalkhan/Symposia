using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Threading.RateLimiting;

namespace Symposia.Blockchain.Gateway.Rpc;

/// <summary>
/// Abuse controls for <c>/v1/rpc</c> (issue #182): a token bucket per client and a gateway-wide
/// concurrency cap, neither of which queues. Called from the handler rather than as middleware,
/// because a call's price depends on its parsed method. Tokens are never refunded, so probing
/// with refused calls isn't free. Idle buckets are evicted by the partitioned limiter once full.
/// </summary>
public sealed class RpcProxyLimiter : IDisposable
{
    public const string MeterName = "Symposia.Blockchain.Gateway.RpcProxy";
    private const int MaxLogThrottleEntries = 10_000;

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Forwarded = Meter.CreateCounter<long>("rpc_proxy.forwarded");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("rpc_proxy.rejected");

    private readonly RpcRateLimitOptions _options;
    private readonly IReadOnlyList<IPNetwork> _trusted;
    private readonly PartitionedRateLimiter<string>? _buckets;
    private readonly ConcurrencyLimiter? _inFlight;
    private readonly ConcurrentDictionary<string, long> _lastLoggedMinute = new();

    public RpcProxyLimiter(RpcRateLimitOptions options)
    {
        _options = options;
        _trusted = [.. options.TrustedProxies.Select(p => RpcClientKey.TryParseNetwork(p, out var n) ? n : throw new ArgumentException(p))];
        if (!options.Enabled)
        {
            return;
        }

        _buckets = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = options.BurstTokens,
            TokensPerPeriod = options.TokensPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
        _inFlight = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = options.MaxConcurrentRequests, QueueLimit = 0 });
    }

    public int GetLogsCost => _options.GetLogsCost;

    public string ClientOf(HttpContext http) =>
        RpcClientKey.Resolve(http.Connection.RemoteIpAddress, http.Request.Headers["X-Forwarded-For"].ToString(), _trusted);

    /// <summary>Takes <paramref name="tokens"/> from the client's bucket; returns the Retry-After seconds when it's short.</summary>
    public int? TryTake(string client, int tokens)
    {
        if (_buckets is null || tokens <= 0)
        {
            return null;
        }

        using var lease = _buckets.AttemptAcquire(client, tokens);
        if (lease.IsAcquired)
        {
            return null;
        }

        var available = _buckets.GetStatistics(client)?.CurrentAvailablePermits ?? 0;
        return Math.Max(1, (int)Math.Ceiling((double)(tokens - Math.Max(0, available)) / _options.TokensPerSecond));
    }

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

    public static void CountForwarded() => Forwarded.Add(1);

    public static void CountRejected(string reason) => Rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>True at most once per client per minute, so a flood can't also flood the logs.</summary>
    public bool ShouldLog(string client)
    {
        var minute = Environment.TickCount64 / 60_000;
        if (_lastLoggedMinute.Count > MaxLogThrottleEntries)
        {
            _lastLoggedMinute.Clear();
        }

        var previous = _lastLoggedMinute.GetOrAdd(client, -1);
        return previous != minute && _lastLoggedMinute.TryUpdate(client, minute, previous);
    }

    public void Dispose()
    {
        _buckets?.Dispose();
        _inFlight?.Dispose();
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }
}
