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

    public static void AddRpcProxy(this IServiceCollection services, RpcProxyOptions options)
    {
        services.AddSingleton<LatestBlockCache>();
        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = options.MaxConnections });
    }

    public static void MapRpcProxy(this WebApplication app)
    {
        app.MapPost("/v1/rpc", async (HttpContext http, IOptions<GatewayOptions> gatewayOptions, IHttpClientFactory clients,
            LatestBlockCache latest, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var gateway = gatewayOptions.Value;
            var options = gateway.RpcProxy;
            var logger = loggers.CreateLogger(typeof(RpcProxyEndpoints));

            var body = await ReadBoundedAsync(http.Request.Body, options.MaxRequestBytes, ct);
            if (body is null)
            {
                return Error(null, -32600, $"Request exceeds {options.MaxRequestBytes} bytes");
            }

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                return Error(null, -32700, "Parse error");
            }

            if (request is JsonArray)
            {
                // A batch would let one allowed envelope smuggle disallowed calls; nothing batches today.
                return Error(null, -32600, "Batch requests are not supported on the gateway's read-only RPC");
            }

            if (request is not JsonObject call || call["method"] is not JsonValue methodNode || !methodNode.TryGetValue(out string? method))
            {
                return Error(null, -32600, "Invalid request");
            }

            var id = call["id"]?.DeepClone();
            if (!AllowedMethods.Contains(method))
            {
                logger.LogInformation("RPC proxy rejected method {Method} from {Remote}.", method, http.Connection.RemoteIpAddress);
                return Error(id, -32601, $"Method '{method}' is not available on the gateway's read-only RPC");
            }

            var parameters = call["params"] as JsonArray;
            if (method == "eth_call" && parameters is { Count: > 2 })
            {
                return Error(id, -32602, "State overrides are not supported on the gateway's read-only RPC");
            }

            if (method == "eth_getLogs")
            {
                string? rangeError;
                try
                {
                    rangeError = await CheckLogRangeAsync(parameters, options.MaxLogBlockRange,
                        () => latest.GetAsync(clients.CreateClient(HttpClientName), gateway.RpcUrl, ct));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("RPC proxy could not read the latest block: {Message}", ex.Message);
                    return Error(id, -32603, "Internal error");
                }

                if (rangeError is not null)
                {
                    logger.LogInformation("RPC proxy rejected eth_getLogs from {Remote}: {Reason}.", http.Connection.RemoteIpAddress, rangeError);
                    return Error(id, -32005, rangeError);
                }
            }

            try
            {
                using var upstream = await clients.CreateClient(HttpClientName).PostAsync(gateway.RpcUrl,
                    new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } }, ct);
                var response = await ReadBoundedAsync(await upstream.Content.ReadAsStreamAsync(ct), options.MaxResponseBytes, ct);
                if (response is null)
                {
                    return Error(id, -32005, $"Response exceeds {options.MaxResponseBytes} bytes");
                }

                return Results.Bytes(response, "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("RPC proxy could not reach the upstream RPC: {Message}", ex.Message);
                return Error(id, -32603, "Internal error");
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

    private static IResult Error(JsonNode? id, int code, string message) => Results.Text(
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(),
        "application/json", Encoding.UTF8);
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
            var hex = json?["result"]?.GetValue<string>() ?? throw new HttpRequestException("eth_blockNumber returned no result");
            _cached = (DateTimeOffset.UtcNow, ulong.Parse(hex.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            return _cached.Block;
        }
        finally
        {
            _gate.Release();
        }
    }
}
