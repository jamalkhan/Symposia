using Symposia.Identity.Domain;
using Symposia.Identity.Gateway.Siwe;

namespace Symposia.Identity.Gateway.Endpoints;

public static class IdentityEndpoints
{
    public static void Map(WebApplication app)
    {
        // FR3, TC-2.1: issue a SIWE challenge for a wallet address.
        app.MapPost("/v1/identity/challenge", (ChallengeRequest request, SiweChallengeService siwe) =>
        {
            if (!WalletAddress.TryParse(request.Wallet, out var wallet))
            {
                return Results.BadRequest(new { error = "invalid wallet address" });
            }

            var challenge = siwe.IssueChallenge(wallet);
            return Results.Ok(new
            {
                message = challenge.ToMessage(),
                nonce = challenge.Nonce,
                issuedAt = challenge.IssuedAt,
                expirationTime = challenge.ExpirationTime,
            });
        });

        // FR3, FR9, AC1: verify a signed challenge; lazily creates the identity binding
        // on first success — not before (TC-1.5, TC-1.6).
        app.MapPost("/v1/identity/verify", (VerifyRequest request, SiweChallengeService siwe, IIdentityBindingStore bindings) =>
        {
            if (!siwe.TryVerify(request.Nonce, request.Signature, out var wallet))
            {
                return Results.Json(new { error = "signature verification failed" }, statusCode: 401);
            }

            var identityId = bindings.GetOrCreate(wallet, createdVia: "siwe_verify");
            return Results.Ok(new { identityId, wallet = wallet.Value });
        });

        // FR7, AC1: resolve identity_id <-> wallet address, either direction.
        app.MapGet("/v1/identity/resolve", (string? wallet, Guid? identityId, IIdentityBindingStore bindings) =>
        {
            if (wallet is not null)
            {
                if (!WalletAddress.TryParse(wallet, out var parsed))
                {
                    return Results.BadRequest(new { error = "invalid wallet address" });
                }

                var id = bindings.TryResolveByWallet(parsed);
                return id is null ? Results.NotFound() : Results.Ok(new { identityId = id, wallet = parsed.Value });
            }

            if (identityId is not null)
            {
                var resolved = bindings.TryResolveByIdentityId(identityId.Value);
                return resolved is null ? Results.NotFound() : Results.Ok(new { identityId, wallet = resolved.Value.Value });
            }

            return Results.BadRequest(new { error = "wallet or identityId is required" });
        });
    }

    public sealed record ChallengeRequest(string Wallet);

    public sealed record VerifyRequest(string Nonce, string Signature);
}
