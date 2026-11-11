using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Symposia.Blockchain.Gateway.Rpc;

/// <summary>
/// Read-only JSON-RPC proxy (issue #172): lets a joined host read the chain through the gateway
/// it joined, without the gateway exposing its upstream RPC (often loopback, or a provider URL
/// with an API key). Only a fixed allowlist of read methods is forwarded, verbatim; everything
/// else is answered with a JSON-RPC error and never reaches the upstream. Writes stay on the relay.
/// </summary>
public static class RpcProxyEndpoints
{
    public const string HttpClientName = "upstream-rpc";

    /// <summary>Widening this is a reviewed code change on purpose, not configuration.</summary>
    internal static readonly FrozenSet<string> AllowedMethods = FrozenSet.ToFrozenSet(
    [
        "eth_chainId", "eth_blockNumber", "eth_call", "eth_getLogs", "eth_getBlockByNumber",
        "eth_getBlockByHash", "eth_getTransactionReceipt", "eth_getCode",
    ], StringComparer.Ordinal);

    /// <summary>
    /// Also allowed on a LocalDev gateway (<see cref="DevFundingOptions.Enabled"/>, #200), so joining
    /// hosts can send their own signed staking transactions to the anvil chain. Never on other networks.
    /// </summary>
    internal static readonly FrozenSet<string> LocalDevWriteMethods = FrozenSet.ToFrozenSet(
    [
        "eth_sendRawTransaction", "eth_getTransactionCount", "eth_estimateGas", "eth_gasPrice",
        "eth_maxPriorityFeePerGas", "eth_feeHistory", "eth_getBalance", "eth_getTransactionByHash",
    ], StringComparer.Ordinal);

    public static void AddRpcProxy(this IServiceCollection services, RpcProxyOptions options)
    {
        services.AddSingleton<LatestBlockCache>();
        services.AddSingleton(sp => new RpcProxyLimiter(options.RateLimit, sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = options.MaxConnections });
    }

    public static void MapRpcProxy(this WebApplication app)
    {
        app.MapPost("/v1/rpc", async (HttpContext http, IOptions<GatewayOptions> gatewayOptions, IHttpClientFactory clients,
            LatestBlockCache latest, RpcProxyLimiter limiter, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var gateway = gatewayOptions.Value;
            var options = gateway.RpcProxy;
            var logger = loggers.CreateLogger(typeof(RpcProxyEndpoints));

            // The bucket comes first (#182): an over-budget client is refused before its body is read or parsed.
            var client = limiter.ClientOf(http);
            if (limiter.TryTake(client, 1) is { } wait)
            {
                return Limited(http, null, wait, "rate limit exceeded", "rate_limited", client, limiter, logger);
            }

            // A body trickled in slowly must not hold the call open, so reading it has a deadline.
            byte[]? body;
            using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readDeadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                try
                {
                    body = await ReadBoundedAsync(http.Request.Body, options.MaxRequestBytes, readDeadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Error(limiter, null, -32600, $"Request body not received within {options.TimeoutSeconds} s");
                }
            }

            if (body is null)
            {
                return Error(limiter, null, -32600, $"Request exceeds {options.MaxRequestBytes} bytes");
            }

            // The slot guards upstream work only, so it's taken once the body is in hand (#182 review).
            using var slot = limiter.TryEnter();
            if (slot is null)
            {
                return Limited(http, null, 1, "too many concurrent requests on the gateway", "concurrency", client, limiter, logger);
            }

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                return Error(limiter, null, -32700, "Parse error");
            }

            if (request is JsonArray)
            {
                // A batch would let one allowed envelope smuggle disallowed calls; nothing batches today.
                return Error(limiter, null, -32600, "Batch requests are not supported on the gateway's read-only RPC");
            }

            string? method;
            JsonNode? id, rawParams;
            try
            {
                if (request is not JsonObject call || call["method"] is not JsonValue methodNode || !methodNode.TryGetValue(out method))
                {
                    return Error(limiter, null, -32600, "Invalid request");
                }

                id = call["id"]?.DeepClone();
                rawParams = call["params"]?.DeepClone();
            }
            catch (ArgumentException)
            {
                return Error(limiter, null, -32600, "Invalid request: duplicate keys"); // System.Text.Json refuses duplicate property names
            }

            if (!AllowedMethods.Contains(method) && !(gateway.DevFunding.Enabled && LocalDevWriteMethods.Contains(method)))
            {
                if (limiter.ShouldLog(client))
                {
                    logger.LogInformation("RPC proxy rejected method {Method} from {Client}.", method, client);
                }

                return Error(limiter, id, -32601, $"Method '{method}' is not available on the gateway's read-only RPC", "method");
            }

            var parameters = rawParams as JsonArray;
            if (rawParams is not null && parameters is null)
            {
                return Error(limiter, id, -32602, "params must be an array");
            }
            if (method == "eth_call" && parameters is { Count: > 2 })
            {
                return Error(limiter, id, -32602, "State overrides are not supported on the gateway's read-only RPC");
            }

            if (method == "eth_getLogs")
            {
                // Before the range check, which can itself read the upstream's latest block.
                if (limiter.TryTakeGetLogs(client) is { } logsWait)
                {
                    return Limited(http, id, logsWait, "rate limit exceeded", "rate_limited", client, limiter, logger);
                }

                string? rangeError;
                try
                {
                    rangeError = await CheckLogRangeAsync(parameters, options.MaxLogBlockRange,
                        () => latest.GetAsync(clients.CreateClient(HttpClientName), gateway.RpcUrl, ct));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("RPC proxy could not read the latest block: {Message}", ex.Message);
                    return Error(limiter, id, -32603, "Internal error", null);
                }

                if (rangeError is not null)
                {
                    if (limiter.ShouldLog(client))
                    {
                        logger.LogInformation("RPC proxy rejected eth_getLogs from {Client}: {Reason}.", client, rangeError);
                    }

                    return Error(limiter, id, -32005, rangeError);
                }
            }

            try
            {
                // Forward only what was validated, rebuilt here: upstreams that match keys case-insensitively
                // (Go's encoding/json, e.g. geth) would otherwise run a smuggled "METHOD" or "PARAMS" (#172 review).
                var canonical = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["method"] = method, ["params"] = parameters?.DeepClone() ?? new JsonArray() };
                using var upstream = await clients.CreateClient(HttpClientName).PostAsync(gateway.RpcUrl,
                    new StringContent(canonical.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                var response = await ReadBoundedAsync(await upstream.Content.ReadAsStreamAsync(ct), options.MaxResponseBytes, ct);
                if (response is null)
                {
                    return Error(limiter, id, -32005, $"Response exceeds {options.MaxResponseBytes} bytes", null);
                }

                limiter.CountForwarded();
                return Results.Bytes(response, "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("RPC proxy could not reach the upstream RPC: {Message}", ex.Message);
                return Error(limiter, id, -32603, "Internal error", null);
            }
        });
    }

    /// <summary>Returns why an <c>eth_getLogs</c> filter is refused, or null when it fits the cap.</summary>
    internal static async Task<string?> CheckLogRangeAsync(JsonArray? parameters, ulong cap, Func<Task<ulong>> latestBlock)
    {
        if (parameters is not [JsonObject filter])
        {
            return "eth_getLogs takes exactly one filter object";
        }

        if (filter.ContainsKey("blockHash"))
        {
            return filter.ContainsKey("fromBlock") || filter.ContainsKey("toBlock") ? "blockHash cannot be combined with a block range" : null;
        }

        var from = await ResolveAsync(filter["fromBlock"], latestBlock);
        var to = await ResolveAsync(filter["toBlock"], latestBlock);
        if (from is null || to is null)
        {
            return "eth_getLogs block range could not be resolved";
        }

        if (to < from)
        {
            return "eth_getLogs toBlock is before fromBlock";
        }

        return to.Value - from.Value + 1 > cap ? $"eth_getLogs block range exceeds {cap}" : null;
    }

    private static async Task<ulong?> ResolveAsync(JsonNode? tag, Func<Task<ulong>> latestBlock)
    {
        if (tag is null)
        {
            return await latestBlock();
        }

        if (tag is not JsonValue value || !value.TryGetValue(out string? text))
        {
            return null;
        }

        return text switch
        {
            "earliest" => 0,
            "latest" or "safe" or "finalized" or "pending" => await latestBlock(),
            _ when text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>A JSON-RPC error; <paramref name="reason"/> null means an upstream failure, not a rejection.</summary>
    private static IResult Error(RpcProxyLimiter limiter, JsonNode? id, int code, string message, string? reason = "invalid", int? statusCode = null)
    {
        if (reason is null)
        {
            limiter.CountUpstreamError();
        }
        else
        {
            limiter.CountRejected(reason);
        }

        return Results.Text(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(),
            "application/json", Encoding.UTF8, statusCode);
    }

    /// <summary>HTTP 429 with Retry-After and a <c>-32005</c> body; the call never reaches the upstream (#182).</summary>
    private static IResult Limited(HttpContext http, JsonNode? id, int retryAfterSeconds, string message, string reason, string client,
        RpcProxyLimiter limiter, ILogger logger)
    {
        if (limiter.ShouldLog(client))
        {
            logger.LogInformation("RPC proxy limited {Client}: {Reason}.", client, message);
        }

        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Error(limiter, id, -32005, $"Limit exceeded: {message} on the gateway's read-only RPC", reason, StatusCodes.Status429TooManyRequests);
    }
}

/// <summary>The upstream's latest block, cached for a second so range checks don't double upstream load.</summary>
public sealed class LatestBlockCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, ulong Block) _cached = (DateTimeOffset.MinValue, 0);

    public async Task<ulong> GetAsync(HttpClient client, string rpcUrl, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _cached.At < TimeSpan.FromSeconds(1))
            {
                return _cached.Block;
            }

            using var response = await client.PostAsync(rpcUrl, new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"eth_blockNumber","params":[]}""", Encoding.UTF8, "application/json"), ct);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            // Anything but a hex result (an upstream error object, say) is an upstream failure, answered as -32603.
            if (json?["result"] is not JsonValue result || !result.TryGetValue(out string? hex) || !hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !ulong.TryParse(hex.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var block))
            {
                throw new HttpRequestException("eth_blockNumber returned no usable result");
            }

            _cached = (DateTimeOffset.UtcNow, block);
            return _cached.Block;
        }
        finally
        {
            _gate.Release();
        }
    }
}
