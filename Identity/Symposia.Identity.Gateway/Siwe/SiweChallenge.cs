using Symposia.Identity.Domain;

namespace Symposia.Identity.Gateway.Siwe;

/// <summary>An issued, not-yet-consumed EIP-4361 sign-in challenge (FR3).</summary>
public sealed record SiweChallenge(
    WalletAddress Wallet,
    string Domain,
    string Nonce,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpirationTime)
{
    /// <summary>The exact message the wallet is expected to sign (personal_sign).</summary>
    public string ToMessage() =>
        $"{Domain} wants you to sign in with your Ethereum account:\n" +
        $"{Wallet}\n\n" +
        "Sign in to Symposia to prove control of this wallet.\n\n" +
        $"URI: https://{Domain}\n" +
        "Version: 1\n" +
        $"Nonce: {Nonce}\n" +
        $"Issued At: {IssuedAt:O}\n" +
        $"Expiration Time: {ExpirationTime:O}";
}
