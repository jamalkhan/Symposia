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
}
