using Symposia.Identity.Domain;

namespace Symposia.Identity.Gateway.Tests;

public class SymposiaIdentityIdTests
{
    // TC-1.1, AC1: identity_id is deterministically derivable from the wallet address.
    [Fact]
    public void Derive_IsDeterministic()
    {
        var wallet = WalletAddress.Parse("0xABC1230000000000000000000000000000000AaB");
        var first = SymposiaIdentityId.Derive(wallet);
        var second = SymposiaIdentityId.Derive(wallet);
        Assert.Equal(first, second);
    }

    // TC-1.2: resolving back from the derived id is consistent (the id round-trips
    // to the same value given the same normalized address, immediately after creation).
    [Fact]
    public void Derive_IsCaseInsensitiveOnInput()
    {
        var lower = WalletAddress.Parse("0xabc1230000000000000000000000000000000aab");
        var mixed = WalletAddress.Parse("0xAbC1230000000000000000000000000000000AaB");
        Assert.Equal(SymposiaIdentityId.Derive(lower), SymposiaIdentityId.Derive(mixed));
    }

    // TC-1.3: two independent wallets produce non-colliding identity ids.
    [Fact]
    public void Derive_DistinctWallets_ProduceDistinctIds()
    {
        var a = WalletAddress.Parse("0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var b = WalletAddress.Parse("0xBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        Assert.NotEqual(SymposiaIdentityId.Derive(a), SymposiaIdentityId.Derive(b));
    }

    [Fact]
    public void Derive_ProducesRfc4122Version5Guid()
    {
        var wallet = WalletAddress.Parse("0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var id = SymposiaIdentityId.Derive(wallet);
        var bytes = id.ToByteArray();

        Assert.Equal(0x50, bytes[7] & 0xF0); // version nibble
        Assert.Equal(0x80, bytes[8] & 0xC0); // RFC 4122 variant
    }
}
