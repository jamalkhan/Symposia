using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Symposia.Network.Node.Tests;

/// <summary>Wire format against the Blockchain Gateway (issues #109, #110, #129).</summary>
public sealed class NodeRegistryClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private const string Address = "0x5FbDB2315678afecb367f032d93F642f64180aa3";

    private static NodeRegistryClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://gateway.local") });

    [Fact]
    public async Task RegisterAsync_SendsNodeEndpointNonceAndHexSignature()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Client(handler).RegisterAsync(Address, "http://a:5180", 3, [0xAB, 0xCD]);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(Address, body.RootElement.GetProperty("node").GetString());
        Assert.Equal("http://a:5180", body.RootElement.GetProperty("endpoint").GetString());
        Assert.Equal(3UL, body.RootElement.GetProperty("nonce").GetUInt64());
        Assert.Equal("0xABCD", body.RootElement.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task RegisterAsync_RejectedByGateway_Throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).RegisterAsync(Address, "http://a:5180", 0, new byte[65]));
    }

    [Fact]
    public async Task GetStatusAsync_ParsesRegistrationEndpointAndNonce()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { node = Address, registered = true, endpoint = "http://a:5180", nonce = 2 }),
        });

        var status = await Client(handler).GetStatusAsync(Address);

        Assert.Equal(new NodeRegistryStatus(true, "http://a:5180", 2), status);
    }

    [Fact]
    public async Task ListNodesAsync_ParsesMembers()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[] { new { node = Address, endpoint = "http://a:5180" } }),
        });

        var nodes = await Client(handler).ListNodesAsync();

        Assert.Equal([new RegisteredNode(Address, "http://a:5180")], nodes);
    }
}
