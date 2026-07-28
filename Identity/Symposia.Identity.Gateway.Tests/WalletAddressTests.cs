using Symposia.Identity.Domain;

namespace Symposia.Identity.Gateway.Tests;

public class WalletAddressTests
{
    // TC-1.7: canonical address format is consistent regardless of input case.
    [Fact]
    public void Parse_NormalizesCase()
    {
        var mixed = WalletAddress.Parse("0xAbC1230000000000000000000000000000000AaB");
        var lower = WalletAddress.Parse("0xabc1230000000000000000000000000000000aab");
        Assert.Equal(lower, mixed);
    }

    // TC-6.5: malformed addresses are rejected, not silently accepted.
    [Theory]
    [InlineData("not-an-address")]
    [InlineData("0x123")]
    [InlineData("")]
    public void TryParse_RejectsMalformedInput(string raw)
    {
        Assert.False(WalletAddress.TryParse(raw, out _));
    }

    [Fact]
    public void Parse_RejectsMalformedInput_Throws()
    {
        Assert.Throws<FormatException>(() => WalletAddress.Parse("0xnothex"));
    }
}
