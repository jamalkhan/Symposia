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
        var signature = Eip712TestSigner.SignRegister(privateKey, address, _fixture.NodeRegistryAddress, ChainFixture.ChainId);

        var resp = await _client.PostAsJsonAsync("/v1/nodes/register",
            new { node = address, signature = "0x" + Convert.ToHexString(signature) });

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
            Eip712TestSigner.SignRegister(privateKey, address, _fixture.NodeRegistryAddress, ChainFixture.ChainId));

        var first = await _client.PostAsJsonAsync("/v1/nodes/register", new { node = address, signature });
        var second = await _client.PostAsJsonAsync("/v1/nodes/register", new { node = address, signature });

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

    private async Task RegisterAsync(string address, string privateKey)
    {
        var signature = "0x" + Convert.ToHexString(
            Eip712TestSigner.SignRegister(privateKey, address, _fixture.NodeRegistryAddress, ChainFixture.ChainId));
        var resp = await _client.PostAsJsonAsync("/v1/nodes/register", new { node = address, signature });
        resp.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SubmitRootAsync(string address, string privateKey, ulong epoch, string root)
    {
        var sig = "0x" + Convert.ToHexString(Eip712TestSigner.SignSubmitRoot(
            privateKey, address, epoch, Convert.FromHexString(root[2..]), _fixture.EpochRootRegistryAddress, ChainFixture.ChainId));
        return await _client.PostAsJsonAsync($"/v1/nodes/{address}/epoch-roots", new { epoch, root, signature = sig });
    }
}
