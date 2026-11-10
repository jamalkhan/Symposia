using System.Net;
using System.Net.Sockets;
using Symposia.NodeHost.Ledger;

namespace Symposia.NodeHost.Tests;

/// <summary>
/// #182 review: the real Nethereum-backed source against a gateway answering 429. The fix relies on
/// Nethereum surfacing an HTTP 429 as a transport failure rather than an RPC error, so pin that.
/// </summary>
public sealed class LedgerRateLimitTests
{
    [Fact]
    public async Task StartBlockSearch_PropagatesA429_InsteadOfPinningAStartBlock()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var calls = 0;
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var limited = Interlocked.Increment(ref calls) > 1; // eth_blockNumber succeeds, eth_getCode is limited
                ctx.Response.StatusCode = limited ? 429 : 200;
                ctx.Response.ContentType = "application/json";
                var body = limited
                    ? """{"jsonrpc":"2.0","id":1,"error":{"code":-32005,"message":"Limit exceeded"}}"""
                    : """{"jsonrpc":"2.0","id":1,"result":"0x64"}""";
                await ctx.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body));
                ctx.Response.Close();
            }
        });

        var source = new NethereumLedgerLogSource($"http://127.0.0.1:{port}/", "0x5FbDB2315678afecb367f032d93F642f64180aa3",
            "0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512");

        await Assert.ThrowsAnyAsync<Exception>(() => source.StartBlockAsync(default));

        listener.Stop();
        await serve;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
