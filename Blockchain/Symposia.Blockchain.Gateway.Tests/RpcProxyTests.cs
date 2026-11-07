using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>Issue #172: the read-only <c>/v1/rpc</c> proxy joined hosts read the chain through.</summary>
[Collection("ChainIntegration")]
public sealed class RpcProxyTests : IClassFixture<ChainFixture>
{
    private readonly ChainFixture _fixture;
    private readonly HttpClient _client;

    public RpcProxyTests(ChainFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    private async Task<JsonElement> RpcAsync(string body)
    {
        var resp = await _client.PostAsync("/v1/rpc", new StringContent(body, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Task<JsonElement> CallAsync(string method, string paramsJson = "[]", int id = 7) =>
        RpcAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""");

    private static int ErrorCode(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();

    private async Task<ulong> BlockNumberAsync()
    {
        var hex = (await CallAsync("eth_blockNumber")).GetProperty("result").GetString()!;
        return Convert.ToUInt64(hex, 16);
    }

    [Fact]
    public async Task AllowedRead_IsForwarded_WithTheRequestId()
    {
        var response = await CallAsync("eth_chainId", id: 42);

        Assert.Equal(42, response.GetProperty("id").GetInt32());
        Assert.Equal(ChainFixture.ChainId, Convert.ToUInt64(response.GetProperty("result").GetString(), 16));
    }

    [Fact]
    public async Task GetCode_ReturnsDeployedContract()
    {
        var response = await CallAsync("eth_getCode", $$"""["{{_fixture.NodeRegistryAddress}}","latest"]""");

        Assert.True(response.GetProperty("result").GetString()!.Length > 2);
    }

    [Theory]
    [InlineData("eth_sendRawTransaction", """["0x00"]""")]
    [InlineData("eth_sendTransaction", """[{}]""")]
    [InlineData("debug_traceTransaction", """["0x00"]""")]
    [InlineData("personal_listAccounts", "[]")]
    public async Task MethodOutsideAllowlist_IsRejected(string method, string parameters)
    {
        Assert.Equal(-32601, ErrorCode(await CallAsync(method, parameters)));
    }

    [Fact]
    public async Task RejectedCall_NeverReachesTheChain()
    {
        var before = await BlockNumberAsync();

        var response = await CallAsync("anvil_mine", """["0x5"]""");

        Assert.Equal(-32601, ErrorCode(response));
        Assert.Equal(before, await BlockNumberAsync());
    }

    [Fact]
    public async Task Batch_IsRejectedWithoutForwarding()
    {
        var before = await BlockNumberAsync();

        var response = await RpcAsync("""[{"jsonrpc":"2.0","id":1,"method":"eth_chainId"},{"jsonrpc":"2.0","id":2,"method":"anvil_mine","params":["0x5"]}]""");

        Assert.Equal(-32600, ErrorCode(response));
        Assert.Equal(before, await BlockNumberAsync());
    }

    [Fact]
    public async Task MalformedJson_IsAParseError()
    {
        Assert.Equal(-32700, ErrorCode(await RpcAsync("{not json")));
    }

    [Fact]
    public async Task EthCall_WithStateOverrides_IsRejected()
    {
        var response = await CallAsync("eth_call", $$"""[{"to":"{{_fixture.NodeRegistryAddress}}","data":"0x"},"latest",{}]""");

        Assert.Equal(-32602, ErrorCode(response));
    }

    [Fact]
    public async Task GetLogs_AtTheCap_IsForwarded()
    {
        var response = await CallAsync("eth_getLogs", """[{"fromBlock":"0x0","toBlock":"0x1387"}]""");

        Assert.Equal(JsonValueKind.Array, response.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task GetLogs_OneBlockPastTheCap_IsRejected()
    {
        var response = await CallAsync("eth_getLogs", """[{"fromBlock":"0x0","toBlock":"0x1388"}]""");

        Assert.Equal(-32005, ErrorCode(response));
    }

    [Theory]
    [InlineData("""[{"fromBlock":"0x10","toBlock":"0x5"}]""")]
    [InlineData("""[{"fromBlock":"bogus"}]""")]
    [InlineData("""[{"blockHash":"0x00","fromBlock":"0x0"}]""")]
    [InlineData("[]")]
    public async Task GetLogs_InvalidRange_IsRejected(string parameters)
    {
        Assert.Equal(-32005, ErrorCode(await CallAsync("eth_getLogs", parameters)));
    }

    [Fact]
    public async Task GetLogs_LatestTags_ResolveAgainstTheChainHead()
    {
        // The test chain is far shorter than the cap, so earliest..latest fits.
        var response = await CallAsync("eth_getLogs", """[{"fromBlock":"earliest","toBlock":"latest"}]""");

        Assert.Equal(JsonValueKind.Array, response.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Descriptor_AdvertisesTheProxy()
    {
        var network = await _client.GetFromJsonAsync<JsonElement>("/v1/network");

        Assert.EndsWith("/v1/rpc", network.GetProperty("rpcUrl").GetString());
    }
}
