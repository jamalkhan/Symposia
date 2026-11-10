using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Symposia.Blockchain.Gateway.Rpc;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// Issue #182: per-client token buckets and the concurrency cap on <c>/v1/rpc</c>. These need no
/// chain: the upstream is a port that refuses connections (or one that never answers), so an
/// allowed call comes back <c>-32603</c> and a limited one comes back 429 without touching it.
/// </summary>
public sealed class RpcProxyLimitTests
{
    private const string ClientHeader = "X-Test-Peer";

    private static WebApplicationFactory<Program> Gateway(string rpcUrl, Dictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // UseSetting, not ConfigureAppConfiguration: GatewayApp reads the proxy options while building.
            var all = new Dictionary<string, string?>
            {
                ["Gateway:RpcUrl"] = rpcUrl,
                ["Gateway:RpcProxy:TimeoutSeconds"] = "5",
                ["Gateway:RpcProxy:RateLimit:TokensPerSecond"] = "1",
                ["Gateway:RpcProxy:RateLimit:BurstTokens"] = "5",
                ["Gateway:RpcProxy:RateLimit:GetLogsCost"] = "5",
            };
            foreach (var (key, value) in settings ?? [])
            {
                all[key] = value;
            }

            foreach (var (key, value) in all)
            {
                b.UseSetting(key, value);
            }

            // TestServer has no peer address; let each test pick one.
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter, PeerFromHeader>());
        });

    private static string RefusedUpstream()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return $"http://127.0.0.1:{port}";
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> CallAsync(HttpClient client, string method = "eth_chainId",
        string? peer = "203.0.113.7", string? forwardedFor = null, string paramsJson = "[]")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rpc")
        {
            Content = new StringContent($$"""{"jsonrpc":"2.0","id":3,"method":"{{method}}","params":{{paramsJson}}}""", Encoding.UTF8, "application/json"),
        };
        if (peer is not null)
        {
            request.Headers.Add(ClientHeader, peer);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        var response = await client.SendAsync(request);
        return (response, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private static int Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetInt32();

    [Fact]
    public async Task OverBudget_Gets429WithRetryAfter_AndNeverReachesTheUpstream()
    {
        await using var gateway = Gateway(RefusedUpstream());
        var client = gateway.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var (ok, body) = await CallAsync(client);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(-32603, Code(body)); // forwarded: the refused upstream answers as an internal error
        }

        var (limited, error) = await CallAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(-32005, Code(error)); // not -32603: the upstream was never contacted
        Assert.True(limited.Headers.RetryAfter!.Delta >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DifferentClients_HaveIndependentBudgets()
    {
        await using var gateway = Gateway(RefusedUpstream());
        var client = gateway.CreateClient();
        for (var i = 0; i < 6; i++)
        {
            await CallAsync(client, peer: "203.0.113.7");
        }

        var (other, _) = await CallAsync(client, peer: "198.51.100.9");

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task GetLogs_CostsGetLogsCostTokens_AndItsRetryAfterCoversThem()
    {
        await using var gateway = Gateway(RefusedUpstream());
        var client = gateway.CreateClient();
        const string range = """[{"fromBlock":"0x1","toBlock":"0x2"}]""";

        await CallAsync(client, "eth_getLogs", paramsJson: range); // spends the whole burst of 5
        var (cheap, _) = await CallAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, cheap.StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(1.2)); // one token back: enough for eth_chainId, not eth_getLogs
        var (logs, _) = await CallAsync(client, "eth_getLogs", paramsJson: range);

        Assert.Equal(HttpStatusCode.TooManyRequests, logs.StatusCode);
        Assert.True(logs.Headers.RetryAfter!.Delta >= TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task RefusedMethods_StillSpendTokens()
    {
        await using var gateway = Gateway(RefusedUpstream());
        var client = gateway.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(-32601, Code((await CallAsync(client, "eth_sendRawTransaction", paramsJson: """["0x00"]""")).Body));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await CallAsync(client)).Response.StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_IsIgnoredUnlessThePeerIsATrustedProxy()
    {
        await using var untrusted = Gateway(RefusedUpstream());
        var client = untrusted.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await CallAsync(client, forwardedFor: $"192.0.2.{i}"); // a fresh claimed client each time
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await CallAsync(client, forwardedFor: "192.0.2.99")).Response.StatusCode);

        await using var proxied = Gateway(RefusedUpstream(), new() { ["Gateway:RpcProxy:RateLimit:TrustedProxies:0"] = "10.0.0.0/8" });
        var viaProxy = proxied.CreateClient();
        for (var i = 0; i < 6; i++)
        {
            await CallAsync(viaProxy, peer: "10.1.2.3", forwardedFor: "192.0.2.1");
        }

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(viaProxy, peer: "10.1.2.3", forwardedFor: "192.0.2.2")).Response.StatusCode);
    }

    [Fact]
    public async Task OverTheConcurrencyCap_IsRefusedImmediately()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0); // accepts, never answers
        silent.Start();
        await using var gateway = Gateway($"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}", new()
        {
            ["Gateway:RpcProxy:RateLimit:MaxConcurrentRequests"] = "2",
            ["Gateway:RpcProxy:RateLimit:BurstTokens"] = "50",
        });
        var client = gateway.CreateClient();
        var hung = new[] { CallAsync(client, peer: "203.0.113.1"), CallAsync(client, peer: "203.0.113.2") };
        await Task.Delay(500);

        var started = DateTime.UtcNow;
        var (refused, body) = await CallAsync(client, peer: "203.0.113.3");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(-32005, Code(body));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        await Task.WhenAll(hung);
    }

    [Fact]
    public async Task Disabled_RestoresUnlimitedBehaviour()
    {
        await using var gateway = Gateway(RefusedUpstream(), new()
        {
            ["Gateway:RpcProxy:RateLimit:Enabled"] = "false",
            ["Gateway:RpcProxy:RateLimit:BurstTokens"] = "0", // not validated when disabled
        });
        var client = gateway.CreateClient();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await CallAsync(client)).Response.StatusCode);
        }
    }

    [Theory]
    [InlineData("TokensPerSecond", "0", "TokensPerSecond")]
    [InlineData("GetLogsCost", "0", "GetLogsCost")]
    [InlineData("BurstTokens", "4", "BurstTokens")]
    [InlineData("MaxConcurrentRequests", "0", "MaxConcurrentRequests")]
    [InlineData("TrustedProxies:0", "not-an-ip", "TrustedProxies")]
    public void InvalidSettings_FailAtStartup_NamingTheKey(string key, string value, string named)
    {
        using var gateway = Gateway(RefusedUpstream(), new() { [$"Gateway:RpcProxy:RateLimit:{key}"] = value });

        var ex = Assert.ThrowsAny<Exception>(() => gateway.CreateClient());

        Assert.Contains($"Gateway:RpcProxy:RateLimit:{named}", ex.ToString());
    }

    [Theory]
    [InlineData(null, null, "unknown")]
    [InlineData("203.0.113.7", "192.0.2.1", "203.0.113.7")] // not a trusted proxy: header ignored
    [InlineData("10.0.0.1", "192.0.2.1", "192.0.2.1")]
    [InlineData("10.0.0.1", "192.0.2.1, 10.0.0.5", "192.0.2.1")] // right-most untrusted hop
    [InlineData("10.0.0.1", "10.0.0.7, 10.0.0.5", "10.0.0.7")] // all trusted: left-most
    [InlineData("10.0.0.1", "garbage", "10.0.0.1")] // malformed: the peer
    [InlineData("::ffff:203.0.113.7", null, "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa::1", null, "2001:db8:1:2::/64")]
    public void ClientKey_ResolvesTheBucket(string? peer, string? forwardedFor, string expected)
    {
        Assert.True(RpcClientKey.TryParseNetwork("10.0.0.0/8", out var proxies));

        Assert.Equal(expected, RpcClientKey.Resolve(peer is null ? null : IPAddress.Parse(peer), forwardedFor, [proxies]));
    }

    [Fact]
    public void IdleBuckets_AreEvicted_AfterAnIpSweep()
    {
        using var limiter = new RpcProxyLimiter(new RpcRateLimitOptions { TokensPerSecond = 100, BurstTokens = 100 });
        for (var i = 0; i < 20_000; i++)
        {
            limiter.TryTake($"198.51.{i / 256}.{i % 256}", 1);
        }

        // Each bucket is full again after a second and idle; the partitioned limiter disposes it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        while (DateTime.UtcNow < deadline && GC.GetTotalMemory(forceFullCollection: true) > before / 2)
        {
            Thread.Sleep(500);
        }

        Assert.True(GC.GetTotalMemory(forceFullCollection: true) <= before / 2 + 4 * 1024 * 1024, "idle buckets were not evicted");
    }

    private sealed class PeerFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, nextMiddleware) =>
            {
                if (http.Request.Headers.TryGetValue(ClientHeader, out var peer))
                {
                    http.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                }

                return nextMiddleware(http);
            });
            next(app);
        };
    }
}
