using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Symposia.Identity.Domain;

namespace Symposia.Identity.Gateway.Siwe;

/// <summary>
/// Issues and verifies EIP-4361-style ("Sign-In with Ethereum") challenges
/// (FR3). Each nonce is single-use (TC-2.4) and expires (TC-2.5); the
/// signature must recover to the exact wallet the challenge was issued for
/// (TC-2.3), over the exact message issued — not a caller-supplied one
/// (TC-2.6, TC-2.7).
/// </summary>
public sealed class SiweChallengeService(IOptions<GatewayOptions> options)
{
    private readonly GatewayOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, SiweChallenge> _issued = new();

    public SiweChallenge IssueChallenge(WalletAddress wallet)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var now = DateTimeOffset.UtcNow;
        var challenge = new SiweChallenge(wallet, _options.SiweDomain, nonce, now, now + _options.ChallengeTtl);
        _issued[nonce] = challenge;
        return challenge;
    }

    /// <summary>
    /// Verifies a signed response to a previously issued challenge. Consumes
    /// the nonce on success or failure, so a given challenge can only ever be
    /// tried once (TC-2.4 also blocks retried failed submissions from being
    /// reused after correction).
    /// </summary>
    public bool TryVerify(string nonce, string signature, out WalletAddress wallet)
    {
        wallet = default;

        if (!_issued.TryRemove(nonce, out var challenge))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow > challenge.ExpirationTime)
        {
            return false;
        }

        string recovered;
        try
        {
            var signer = new EthereumMessageSigner();
            recovered = signer.EncodeUTF8AndEcRecover(challenge.ToMessage(), signature);
        }
        catch
        {
            return false;
        }

        if (!string.Equals(recovered, challenge.Wallet.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        wallet = challenge.Wallet;
        return true;
    }
}
