using System.Net.Http.Json;
using System.Text.Json;
using Nethereum.Util;

namespace Symposia.Database.ComputeNode.Tests;

/// <summary>
/// The compute node uses the shared secp256k1 node identity and exposes the
/// peer handshake/status surface (issue #129 FR-A1, FR-D4).
/// </summary>
public sealed class NodeNetworkIntegrationTests(ComputeNodeApiIntegrationTests.NodeFactory factory)
    : IClassFixture<ComputeNodeApiIntegrationTests.NodeFactory>
{
    [Fact]
    public async Task ComputeNodeId_IsEthereumAddress()
    {
        var body = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/v1/peers");

        Assert.True(AddressUtil.Current.IsValidEthereumAddressHexFormat(body.GetProperty("node").GetString()));
    }
}
