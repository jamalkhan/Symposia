using Symposia.Identity.Gateway.Chain;

namespace Symposia.Identity.Gateway.Tests;

/// <summary>#219: a receipt that used its whole gas limit is reported as out of gas, not as a revert.</summary>
public class ChainFailureTests
{
    [Fact]
    public void FailureMessage_UsedTheWholeLimit_IsOutOfGas()
    {
        var message = EthereumChainClient.FailureMessage("issueCapability", "CapabilityRegistry: no active consent", 107_186, 107_186);

        Assert.Equal("issueCapability ran out of gas (107186 of 107186 used)", message);
    }

    [Theory]
    [InlineData("issueCapability", "CapabilityRegistry: no active consent")]
    [InlineData("GrantConsentFunction", "GrantConsentFunction reverted")]
    public void FailureMessage_UsedLessThanTheLimit_KeepsTheRevertMessage(string name, string revert) =>
        Assert.Equal(revert, EthereumChainClient.FailureMessage(name, revert, 41_000, 107_186));

    [Fact]
    public void FailureMessage_UnknownLimit_KeepsTheRevertMessage() =>
        Assert.Equal("X reverted", EthereumChainClient.FailureMessage("X", "X reverted", 41_000, null));
}
