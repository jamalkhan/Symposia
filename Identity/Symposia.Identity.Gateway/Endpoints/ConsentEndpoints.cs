using System.Text;
using Nethereum.Util;
using Symposia.Identity.Domain;
using Symposia.Identity.Gateway.Chain;

namespace Symposia.Identity.Gateway.Endpoints;

public static class ConsentEndpoints
{
    public static void Map(WebApplication app)
    {
        // FR4, FR6, AC2, AC3: submit a signed consent grant. The chain — not this
        // endpoint — decides whether the signature is valid (TC-3.2, TC-3.3, TC-3.4).
        app.MapPost("/v1/consent/grants", async (GrantRequest request, IChainClient chain) =>
        {
            if (!WalletAddress.TryParse(request.Wallet, out var wallet))
            {
                return Results.BadRequest(new { error = "invalid wallet address" });
            }

            Permission[] permissions;
            try
            {
                permissions = request.Permissions.Select(Domain.PermissionExtensions.ParseWireName).ToArray();
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (string.IsNullOrEmpty(request.GrantSource) || string.IsNullOrEmpty(request.GrantWording))
            {
                return Results.BadRequest(new { error = "grantSource and grantWording are required" });
            }

            try
            {
                var txHash = await chain.GrantConsentAsync(
                    wallet,
                    Keccak(request.TenantId),
                    permissions,
                    Keccak(request.GrantSource),
                    Keccak(request.GrantWording),
                    request.Nonce,
                    request.Deadline,
                    Convert.FromHexString(request.Signature.Replace("0x", "", StringComparison.OrdinalIgnoreCase)));

                return Results.Ok(new { txHash });
            }
            catch (ChainCallException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 422);
            }
        });

        // FR6, TC-5.6: revocation requires the same wallet-signature proof as granting.
        app.MapPost("/v1/consent/revocations", async (RevokeRequest request, IChainClient chain) =>
        {
            if (!WalletAddress.TryParse(request.Wallet, out var wallet))
            {
                return Results.BadRequest(new { error = "invalid wallet address" });
            }

            Permission[] permissions;
            try
            {
                permissions = request.Permissions.Select(Domain.PermissionExtensions.ParseWireName).ToArray();
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            try
            {
                var txHash = await chain.RevokeConsentAsync(
                    wallet,
                    Keccak(request.TenantId),
                    permissions,
                    request.Nonce,
                    request.Deadline,
                    Convert.FromHexString(request.Signature.Replace("0x", "", StringComparison.OrdinalIgnoreCase)));

                return Results.Ok(new { txHash });
            }
            catch (ChainCallException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 422);
            }
        });

        // FR7, AC5: current consent state for a wallet/tenant/permission (read-only, no auth).
        app.MapGet("/v1/consent/state", async (string wallet, string tenantId, string permission, IChainClient chain) =>
        {
            if (!WalletAddress.TryParse(wallet, out var parsedWallet))
            {
                return Results.BadRequest(new { error = "invalid wallet address" });
            }

            Permission parsedPermission;
            try
            {
                parsedPermission = Domain.PermissionExtensions.ParseWireName(permission);
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var state = await chain.GetConsentStateAsync(parsedWallet, Keccak(tenantId), parsedPermission);
            return Results.Ok(new
            {
                granted = state.Granted,
                grantedAt = state.GrantedAt,
                permission,
            });
        });

        // FR4, FR5, AC4: marketer requests a capability token. The chain re-validates
        // consent at mint time (TC-4.2, TC-4.3, TC-4.4); this endpoint just relays.
        app.MapPost("/v1/capability-tokens", async (CapabilityRequest request, IChainClient chain) =>
        {
            if (!WalletAddress.TryParse(request.Wallet, out var wallet))
            {
                return Results.BadRequest(new { error = "invalid wallet address" });
            }

            Permission permission;
            try
            {
                permission = Domain.PermissionExtensions.ParseWireName(request.Permission);
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            try
            {
                var tokenId = await chain.IssueCapabilityAsync(wallet, Keccak(request.TenantId), permission);
                return Results.Ok(new { tokenId });
            }
            catch (ChainCallException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 422);
            }
        });
    }

    // Tenant ids, grant sources, and grant wording are recorded on-chain as
    // bytes32 keccak256 hashes (gas-cheap fixed-width storage); the plaintext
    // lives in the off-chain read model that indexes the emitted events. Must
    // be keccak256 (not any other digest) so it matches what the signing
    // wallet hashes when constructing the EIP-712 struct it signs.
    private static byte[] Keccak(string value) => Sha3Keccack.Current.CalculateHash(Encoding.UTF8.GetBytes(value));

    public sealed record GrantRequest(
        string Wallet,
        string TenantId,
        string[] Permissions,
        string GrantSource,
        string GrantWording,
        ulong Nonce,
        ulong Deadline,
        string Signature);

    public sealed record RevokeRequest(
        string Wallet, string TenantId, string[] Permissions, ulong Nonce, ulong Deadline, string Signature);

    public sealed record CapabilityRequest(string Wallet, string TenantId, string Permission);
}
