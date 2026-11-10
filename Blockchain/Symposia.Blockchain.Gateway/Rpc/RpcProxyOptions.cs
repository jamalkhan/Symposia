namespace Symposia.Blockchain.Gateway;

/// <summary>The read-only JSON-RPC proxy joined hosts read the chain through (issue #172).</summary>
public sealed class RpcProxyOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Largest inclusive block span one <c>eth_getLogs</c> may cover.</summary>
    public ulong MaxLogBlockRange { get; set; } = 5_000;

    public int MaxRequestBytes { get; set; } = 256 * 1024;

    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Upstream connections reserved for proxied reads, so they can't starve the sealer and relay.</summary>
    public int MaxConnections { get; set; } = 16;

    public RpcRateLimitOptions RateLimit { get; set; } = new();
}

/// <summary>
/// Per-client and gateway-wide limits on the proxy (issue #182). Each client IP gets a token bucket;
/// a call costs one token and <c>eth_getLogs</c> costs <see cref="GetLogsCost"/>. Clients behind one
/// NAT share a bucket. With several gateways, each limits on its own.
/// </summary>
public sealed class RpcRateLimitOptions
{
    /// <summary>False turns off both the per-client bucket and <see cref="MaxConcurrentRequests"/>.</summary>
    public bool Enabled { get; set; } = true;

    public int TokensPerSecond { get; set; } = 20;

    public int BurstTokens { get; set; } = 200;

    public int GetLogsCost { get; set; } = 5;

    /// <summary>Proxied calls in flight across all clients; beyond this, calls are refused, never queued.</summary>
    public int MaxConcurrentRequests { get; set; } = 64;

    /// <summary>
    /// Reverse proxies (IPs or CIDRs) whose <c>X-Forwarded-For</c> is believed. Empty = forwarded
    /// headers are ignored, so a client can't claim a fresh bucket.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>The first invalid setting, named by its configuration key, or null when all are valid.</summary>
    internal string? Validate()
    {
        if (!Enabled)
        {
            return null;
        }

        const string prefix = "Gateway:RpcProxy:RateLimit:";
        if (TokensPerSecond < 1)
        {
            return $"{prefix}TokensPerSecond must be at least 1.";
        }

        if (GetLogsCost < 1)
        {
            return $"{prefix}GetLogsCost must be at least 1.";
        }

        if (BurstTokens < GetLogsCost)
        {
            return $"{prefix}BurstTokens must be at least GetLogsCost ({GetLogsCost}).";
        }

        if (MaxConcurrentRequests < 1)
        {
            return $"{prefix}MaxConcurrentRequests must be at least 1.";
        }

        var bad = TrustedProxies.FirstOrDefault(p => !Rpc.RpcClientKey.TryParseNetwork(p, out _));
        return bad is null ? null : $"{prefix}TrustedProxies has an entry that is not an IP address or CIDR: '{bad}'.";
    }
}
