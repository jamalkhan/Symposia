using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Symposia.Identity.Domain;
using Symposia.Identity.Gateway;
using Symposia.Identity.Gateway.Siwe;

namespace Symposia.Identity.Gateway.Tests;

public class SiweChallengeServiceTests
{
    private readonly SiweChallengeService _service = new(Options.Create(new GatewayOptions
    {
        SiweDomain = "symposia.test",
        ChallengeTtl = TimeSpan.FromMinutes(5),
    }));

    // TC-2.1, TC-2.2: a wallet signs the exact issued challenge and verification succeeds.
    [Fact]
    public void IssueAndVerify_ValidSignature_Succeeds()
    {
        var key = new EthECKey(Nethereum.Signer.EthECKey.GenerateKey().GetPrivateKeyAsBytes(), true);
        var wallet = WalletAddress.Parse(key.GetPublicAddress());

        var challenge = _service.IssueChallenge(wallet);
        var signer = new EthereumMessageSigner();
        var signature = signer.EncodeUTF8AndSign(challenge.ToMessage(), key);

        Assert.True(_service.TryVerify(challenge.Nonce, signature, out var recovered));
        Assert.Equal(wallet, recovered);
    }

    // TC-2.3: a challenge response signed by the wrong private key is rejected.
    [Fact]
    public void Verify_WrongSigner_Rejected()
    {
        var key = new EthECKey(EthECKey.GenerateKey().GetPrivateKeyAsBytes(), true);
        var wallet = WalletAddress.Parse(key.GetPublicAddress());
        var otherKey = new EthECKey(EthECKey.GenerateKey().GetPrivateKeyAsBytes(), true);

        var challenge = _service.IssueChallenge(wallet);
        var signer = new EthereumMessageSigner();
        var signature = signer.EncodeUTF8AndSign(challenge.ToMessage(), otherKey);

        Assert.False(_service.TryVerify(challenge.Nonce, signature, out _));
    }

    // TC-2.4: replaying a previously-used valid nonce is rejected.
    [Fact]
    public void Verify_ReplayedNonce_Rejected()
    {
        var key = new EthECKey(EthECKey.GenerateKey().GetPrivateKeyAsBytes(), true);
        var wallet = WalletAddress.Parse(key.GetPublicAddress());

        var challenge = _service.IssueChallenge(wallet);
        var signer = new EthereumMessageSigner();
        var signature = signer.EncodeUTF8AndSign(challenge.ToMessage(), key);

        Assert.True(_service.TryVerify(challenge.Nonce, signature, out _));
        Assert.False(_service.TryVerify(challenge.Nonce, signature, out _));
    }

    // TC-2.5: an expired challenge is rejected.
    [Fact]
    public void Verify_ExpiredChallenge_Rejected()
    {
        var expiredService = new SiweChallengeService(Options.Create(new GatewayOptions
        {
            SiweDomain = "symposia.test",
            ChallengeTtl = TimeSpan.FromMilliseconds(1),
        }));
        var key = new EthECKey(EthECKey.GenerateKey().GetPrivateKeyAsBytes(), true);
        var wallet = WalletAddress.Parse(key.GetPublicAddress());

        var challenge = expiredService.IssueChallenge(wallet);
        Thread.Sleep(20);
        var signer = new EthereumMessageSigner();
        var signature = signer.EncodeUTF8AndSign(challenge.ToMessage(), key);

        Assert.False(expiredService.TryVerify(challenge.Nonce, signature, out _));
    }

    // TC-2.6: a signature over a tampered payload does not recover to the claimed wallet.
    [Fact]
    public void Verify_TamperedMessage_Rejected()
    {
        var key = new EthECKey(EthECKey.GenerateKey().GetPrivateKeyAsBytes(), true);
        var wallet = WalletAddress.Parse(key.GetPublicAddress());

        var challenge = _service.IssueChallenge(wallet);
        var signer = new EthereumMessageSigner();
        var tamperedSignature = signer.EncodeUTF8AndSign(challenge.ToMessage() + "\ntampered", key);

        Assert.False(_service.TryVerify(challenge.Nonce, tamperedSignature, out _));
    }

    // Unknown/garbage nonce (e.g. a never-issued or already-consumed challenge)
    // is rejected outright.
    [Fact]
    public void Verify_UnknownNonce_Rejected()
    {
        Assert.False(_service.TryVerify("does-not-exist", "0xdeadbeef", out _));
    }
}
