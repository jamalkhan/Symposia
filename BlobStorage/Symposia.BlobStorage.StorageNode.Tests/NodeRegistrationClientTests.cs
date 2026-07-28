using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Symposia.BlobStorage.StorageNode.Identity;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>
/// Unit tests for the Bootstrap Chain Gateway HTTP client, exercising the
/// request/response shape agreed with the Gateway's node endpoints (issue
/// #110) against issue #109's QA plan sections 2 and 4 (registration submit,
/// status query) using a stubbed transport rather than a live chain.
/// </summary>
public sealed class NodeRegistrationClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private const string Address = "0x5FbDB2315678afecb367f032d93F642f64180aa";

    // TC-2.1: submitting a signed registration request succeeds.
    [Fact]
    public async Task RegisterAsync_Success_DoesNotThrow()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { node = Address, registered = true, relayed = true }),
        });
        var client = new NodeRegistrationClient(new HttpClient(handler) { BaseAddress = new Uri("http://gateway.local") });

        await client.RegisterAsync(Address, new byte[65]);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    // Verifies the wire format: node address and 0x-prefixed hex signature,
    // matching the Gateway's RegisterRequest(Node, Signature) contract.
    [Fact]
    public async Task RegisterAsync_SendsAddressAndHexSignature()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { node = Address, registered = true, relayed = true }),
        });
        var client = new NodeRegistrationClient(new HttpClient(handler) { BaseAddress = new Uri("http://gateway.local") });

        await client.RegisterAsync(Address, [0xAB, 0xCD]);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(Address, body.RootElement.GetProperty("node").GetString());
        Assert.Equal("0xABCD", body.RootElement.GetProperty("signature").GetString());
    }

    // TC-2.3-2.6: the gateway/contract rejects a forged or malformed
    // registration; the client surfaces this as a thrown exception rather
    // than silently succeeding.
    [Fact]
    public async Task RegisterAsync_RejectedByGateway_Throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { node = Address, error = "NodeRegistry: invalid signature" }),
        });
        var client = new NodeRegistrationClient(new HttpClient(handler) { BaseAddress = new Uri("http://gateway.local") });

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RegisterAsync(Address, new byte[65]));
    }

    // TC-4.1: a registered address reports as registered.
    [Fact]
    public async Task IsRegisteredAsync_RegisteredAddress_ReturnsTrue()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { node = Address, registered = true }),
        });
        var client = new NodeRegistrationClient(new HttpClient(handler) { BaseAddress = new Uri("http://gateway.local") });

        Assert.True(await client.IsRegisteredAsync(Address));
    }

    // TC-4.2: an unregistered address returns a clear "not registered"
    // result, not an error.
    [Fact]
    public async Task IsRegisteredAsync_UnknownAddress_ReturnsFalseWithoutThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { node = Address, registered = false }),
        });
        var client = new NodeRegistrationClient(new HttpClient(handler) { BaseAddress = new Uri("http://gateway.local") });

        Assert.False(await client.IsRegisteredAsync(Address));
    }
}
