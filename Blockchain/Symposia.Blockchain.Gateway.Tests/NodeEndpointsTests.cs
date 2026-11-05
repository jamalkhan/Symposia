using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// End-to-end tests against a real anvil chain with the actual deployed
/// contracts (via <see cref="ChainFixture"/>) and the Gateway's HTTP surface,
/// exercising the acceptance criteria from issue #110.
/// </summary>
[Collection("ChainIntegration")]
public sealed class NodeEndpointsTests : IClassFixture<ChainFixture>
{
    private readonly ChainFixture _fixture;
    private readonly HttpClient _client;

    public NodeEndpointsTests(ChainFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    private static (string Address, string PrivateKeyHex) NewNode()
    {
        var key = EthECKey.GenerateKey();
        return (key.GetPublicAddress(), key.GetPrivateKey());
    }

    // AC2 / Gherkin: a fresh node registers its keypair on-chain.
    [Fact]
    public async Task Register_FreshNode_Succeeds()
    {
        var (address, privateKey) = NewNode();
        var signature = Eip712TestSigner.SignRegister(
            privateKey, address, Endpoint, 0, _fixture.NodeRegistryAddress, ChainFixture.ChainId);

        var resp = await _client.PostAsJsonAsync("/v1/nodes/register",
            new { node = address, endpoint = Endpoint, nonce = 0, signature = "0x" + Convert.ToHexString(signature) });

        resp.EnsureSuccessStatusCode();

        var statusResp = await _client.GetFromJsonAsync<JsonElement>($"/v1/nodes/{address}");
        Assert.True(statusResp.GetProperty("registered").GetBoolean());
    }

    // AC7 / FR10: duplicate registration does not corrupt or duplicate state.
    [Fact]
    public async Task Register_DuplicateAttempt_IsSafeNoOp()
    {
        var (address, privateKey) = NewNode();
        var signature = "0x" + Convert.ToHexString(
            Eip712TestSigner.SignRegister(privateKey, address, Endpoint, 0, _fixture.NodeRegistryAddress, ChainFixture.ChainId));

        var first = await _client.PostAsJsonAsync("/v1/nodes/register", new { node = address, endpoint = Endpoint, nonce = 0, signature });
        var second = await _client.PostAsJsonAsync("/v1/nodes/register", new { node = address, endpoint = Endpoint, nonce = 0, signature });

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        var status = await _client.GetFromJsonAsync<JsonElement>($"/v1/nodes/{address}");
        Assert.True(status.GetProperty("registered").GetBoolean());
    }

    // AC3 / Gherkin: a registered node submits a signed epoch Merkle root.
    [Fact]
    public async Task SubmitEpochRoot_RegisteredNode_IsAcceptedAndReadable()
    {
        var (address, privateKey) = NewNode();
        await RegisterAsync(address, privateKey);

        var root = "0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash("epoch-manifest"u8.ToArray()));
        var sig = "0x" + Convert.ToHexString(Eip712TestSigner.SignSubmitRoot(
            privateKey, address, 0, Convert.FromHexString(root[2..]), _fixture.EpochRootRegistryAddress, ChainFixture.ChainId));

        var submit = await _client.PostAsJsonAsync($"/v1/nodes/{address}/epoch-roots",
            new { epoch = 0, root, signature = sig });
        submit.EnsureSuccessStatusCode();

        var latest = await _client.GetFromJsonAsync<JsonElement>($"/v1/nodes/{address}/epoch-roots/latest");
        Assert.Equal(0, latest.GetProperty("epoch").GetInt64());
        Assert.Equal(root.ToLowerInvariant(), latest.GetProperty("root").GetString()!.ToLowerInvariant());
    }

    // AC4 / Gherkin: an unregistered node's submission is rejected.
    [Fact]
    public async Task SubmitEpochRoot_UnregisteredNode_IsRejected()
    {
        var (address, privateKey) = NewNode();
        var root = "0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash("never-registered"u8.ToArray()));
        var sig = "0x" + Convert.ToHexString(Eip712TestSigner.SignSubmitRoot(
            privateKey, address, 0, Convert.FromHexString(root[2..]), _fixture.EpochRootRegistryAddress, ChainFixture.ChainId));

        var submit = await _client.PostAsJsonAsync($"/v1/nodes/{address}/epoch-roots",
            new { epoch = 0, root, signature = sig });

        Assert.False(submit.IsSuccessStatusCode);

        var latest = await _client.GetAsync($"/v1/nodes/{address}/epoch-roots/latest");
        Assert.Equal(HttpStatusCode.NotFound, latest.StatusCode);
    }

    // AC5 / Gherkin: a forged submission (wrong signer) is rejected.
    [Fact]
    public async Task SubmitEpochRoot_ForgedSignature_IsRejected()
    {
        var (address, privateKey) = NewNode();
        await RegisterAsync(address, privateKey);
        var (_, otherPrivateKey) = NewNode();

        var root = "0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash("forged"u8.ToArray()));
        var forgedSig = "0x" + Convert.ToHexString(Eip712TestSigner.SignSubmitRoot(
            otherPrivateKey, address, 0, Convert.FromHexString(root[2..]), _fixture.EpochRootRegistryAddress, ChainFixture.ChainId));

        var submit = await _client.PostAsJsonAsync($"/v1/nodes/{address}/epoch-roots",
            new { epoch = 0, root, signature = forgedSig });

        Assert.False(submit.IsSuccessStatusCode);
    }

    // AC6 / Gherkin: dispute resolution reads the authoritative root.
    [Fact]
    public async Task GetRoot_HistoricalEpoch_ReturnsExactRoot()
    {
        var (address, privateKey) = NewNode();
        await RegisterAsync(address, privateKey);

        var root0 = "0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash("epoch-0"u8.ToArray()));
        var root1 = "0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash("epoch-1"u8.ToArray()));
        await SubmitRootAsync(address, privateKey, 0, root0);
        await SubmitRootAsync(address, privateKey, 1, root1);

        var historical = await _client.GetFromJsonAsync<JsonElement>($"/v1/nodes/{address}/epoch-roots/0");
        Assert.Equal(root0.ToLowerInvariant(), historical.GetProperty("root").GetString()!.ToLowerInvariant());

        var latest = await _client.GetFromJsonAsync<JsonElement>($"/v1/nodes/{address}/epoch-roots/latest");
        Assert.Equal(1, latest.GetProperty("epoch").GetInt64());
    }

    // AC7 / FR10: duplicate submission for an already-submitted epoch does not corrupt state.
    [Fact]
    public async Task SubmitEpochRoot_IdenticalRetry_IsSafeNoOp()
    {
        var (address, privateKey) = NewNode();
        await RegisterAsync(address, privateKey);

        var root = "0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash("retry-root"u8.ToArray()));
        var first = await SubmitRootAsync(address, privateKey, 0, root);
        var second = await SubmitRootAsync(address, privateKey, 0, root);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
    }

    // A conflicting resubmission (different root, same epoch) is rejected, not silently overwritten.
    [Fact]
    public async Task SubmitEpochRoot_ConflictingRootSameEpoch_IsRejected()
    {
        var (address, privateKey) = NewNode();
        await RegisterAsync(address, privateKey);

        var root = "0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash("original"u8.ToArray()));
        var otherRoot = "0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash("conflicting"u8.ToArray()));
        await SubmitRootAsync(address, privateKey, 0, root);
        var conflict = await SubmitRootAsync(address, privateKey, 0, otherRoot);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    // #129 FR-B5 / TC-6: registered nodes are listed with their endpoints.
    [Fact]
    public async Task ListNodes_IncludesRegisteredNodeWithEndpoint()
    {
        var (address, privateKey) = NewNode();
        await RegisterAsync(address, privateKey, "http://10.1.2.3:5180");

        var nodes = await _client.GetFromJsonAsync<JsonElement>("/v1/nodes");

        Assert.Contains(nodes.EnumerateArray(), n =>
            string.Equals(n.GetProperty("node").GetString(), address, StringComparison.OrdinalIgnoreCase) &&
            n.GetProperty("endpoint").GetString() == "http://10.1.2.3:5180");
    }

    // #129 TC-9: an endpoint change is relayed, not short-circuited as a duplicate.
    [Fact]
    public async Task Register_EndpointChange_IsRelayedAndUpdatesNonce()
    {
        var (address, privateKey) = NewNode();
        await RegisterAsync(address, privateKey, "http://10.0.0.1:5180");
        await RegisterAsync(address, privateKey, "http://10.0.0.2:5180", nonce: 1);

        var status = await _client.GetFromJsonAsync<JsonElement>($"/v1/nodes/{address}");
        Assert.Equal("http://10.0.0.2:5180", status.GetProperty("endpoint").GetString());
        Assert.Equal(2UL, status.GetProperty("nonce").GetUInt64());
    }

    // #129 Gherkin: an endpoint not signed by the node's key is rejected.
    [Fact]
    public async Task Register_EndpointNotSignedByNode_IsRejected()
    {
        var (address, privateKey) = NewNode();
        var signature = "0x" + Convert.ToHexString(Eip712TestSigner.SignRegister(
            privateKey, address, "http://honest:5180", 0, _fixture.NodeRegistryAddress, ChainFixture.ChainId));

        var resp = await _client.PostAsJsonAsync("/v1/nodes/register",
            new { node = address, endpoint = "http://attacker:5180", nonce = 0, signature });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // #133: a node can join knowing only the gateway URL.
    [Fact]
    public async Task Network_DescribesChainAndContracts()
    {
        var network = await _client.GetFromJsonAsync<JsonElement>("/v1/network");

        Assert.Equal(ChainFixture.ChainId, network.GetProperty("chainId").GetUInt64());
        Assert.Equal(_fixture.NodeRegistryAddress, network.GetProperty("nodeRegistryAddress").GetString(), ignoreCase: true);
        Assert.Equal(_fixture.EpochRootRegistryAddress, network.GetProperty("epochRootRegistryAddress").GetString(), ignoreCase: true);
        Assert.Equal(_fixture.ProtocolActivationsAddress, network.GetProperty("protocolActivationsAddress").GetString(), ignoreCase: true);
    }

    // #145 AC6 / TC-13: the schedule lists features with their activation times and the minimum version.
    [Fact]
    public async Task Protocol_ListsScheduledActivations()
    {
        var web3 = new Nethereum.Web3.Web3(new Nethereum.Web3.Accounts.Account(_fixture.RelayerPrivateKey, ChainFixture.ChainId), _fixture.RpcUrl);
        var before = await _client.GetFromJsonAsync<JsonElement>("/v1/protocol");
        var at = before.GetProperty("now").GetUInt64() + 3600;
        var name = "test.feature." + Guid.NewGuid().ToString("N")[..8];

        await web3.Eth.GetContractTransactionHandler<ScheduleFunction>().SendRequestAndWaitForReceiptAsync(
            _fixture.ProtocolActivationsAddress, new ScheduleFunction { Name = name, ActivatesAt = at });

        var schedule = await _client.GetFromJsonAsync<JsonElement>("/v1/protocol");
        Assert.True(schedule.GetProperty("configured").GetBoolean());
        var feature = schedule.GetProperty("features").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
        Assert.Equal(at, feature.GetProperty("activatesAt").GetUInt64());
        Assert.Equal(0u, schedule.GetProperty("minProtocolVersion").GetProperty("effective").GetUInt32());
    }

    [Nethereum.ABI.FunctionEncoding.Attributes.Function("schedule")]
    private sealed class ScheduleFunction : Nethereum.Contracts.FunctionMessage
    {
        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("string", "name", 1)]
        public string Name { get; set; } = "";

        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("uint64", "activatesAt", 2)]
        public ulong ActivatesAt { get; set; }
    }

    private const string Endpoint = "http://127.0.0.1:5180";

    private async Task RegisterAsync(string address, string privateKey, string endpoint = Endpoint, ulong nonce = 0)
    {
        var signature = "0x" + Convert.ToHexString(Eip712TestSigner.SignRegister(
            privateKey, address, endpoint, nonce, _fixture.NodeRegistryAddress, ChainFixture.ChainId));
        var resp = await _client.PostAsJsonAsync("/v1/nodes/register", new { node = address, endpoint, nonce, signature });
        resp.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SubmitRootAsync(string address, string privateKey, ulong epoch, string root)
    {
        var sig = "0x" + Convert.ToHexString(Eip712TestSigner.SignSubmitRoot(
            privateKey, address, epoch, Convert.FromHexString(root[2..]), _fixture.EpochRootRegistryAddress, ChainFixture.ChainId));
        return await _client.PostAsJsonAsync($"/v1/nodes/{address}/epoch-roots", new { epoch, root, signature = sig });
    }
}
