using System.Numerics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nethereum.Contracts;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;
using Symposia.Blockchain.Contracts;
using Symposia.Blockchain.Gateway.Chain;
using Wire = Symposia.Network.Node.Rewards.VerifierAttestationWire;
using Response = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire.Response;

namespace Symposia.Blockchain.Gateway.Rewards;

/// <summary>
/// Relays Verifier attestation batches to <c>VerifierAssignments.attest</c> (#227), paying gas from the relayer with
/// <see cref="GasHeadroom"/>, and a Verifier's self-sync into the pools. The node-key signature is checked off-chain
/// first, so a forged batch never costs gas; the contract checks assignment and window. 409 for terminal reverts
/// (the Verifier stops), 502 for failed receipts or transport (it retries). Because the relayer's gas also pays for
/// seeding, finalize and seals, it is guarded: a batch already relayed (same digest) is answered without sending,
/// each Verifier and each IP is rate-limited, and a sync is sent only when the node is actually stale.
/// </summary>
public static class VerifierRelayEndpoints
{
    private static readonly string[] Terminal =
    [
        "NotAssigned(uint256,address,uint8,address)", "WindowClosed(uint256)", "NotSeeded(uint256)", "InvalidSignature()",
        "AlreadyFinalizing(uint256)", "BadValues(uint256,uint256)", "NotATarget(uint256,address)", "StaleNonce(uint256,uint256)",
    ];


    public static IEndpointRouteBuilder MapVerifierRelay(this IEndpointRouteBuilder app)
    {
        app.MapPost(Wire.Path, async (Wire.Request req, HttpContext http, RelayerAccount relayer, VerifierAssignmentsResolver resolver, VerifierRelayGuard guard, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync() is not { } target)
            {
                return Results.NotFound();
            }

            if (!guard.Allowed("ip:" + http.Connection.RemoteIpAddress) || !guard.Allowed("v:" + req.Verifier?.ToLowerInvariant()))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            if (!AddressUtil.Current.IsValidEthereumAddressHexFormat(req.Verifier) || req.Items is not { Count: > 0 and <= Wire.MaxItems }
                || req.Items.Any(i => !AddressUtil.Current.IsValidEthereumAddressHexFormat(i.Target) || i.Values is not { Count: > 0 and <= 4 }
                    || i.Values.Any(v => !BigInteger.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
                || !req.Signature.IsHex() || req.Signature.HexToByteArray().Length != 65)
            {
                return Results.BadRequest("Malformed attestation batch.");
            }

            if (!string.Equals(Wire.Recover(target.Address, target.ChainId, req), req.Verifier, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new Response(null, "InvalidSignature"), statusCode: StatusCodes.Status409Conflict);
            }

            // A copy of a batch already relayed (from calldata, say) would only burn gas: answer it, don't resend.
            var digest = req.Signature.ToLowerInvariant();
            if (guard.Relayed.TryGetValue(digest, out string? earlier))
            {
                return Results.Ok(new Response(earlier, null));
            }

            var result = await SendAsync(relayer, target.Address, new AttestFunction
            {
                Epoch = req.Epoch, Verifier = req.Verifier, Nonce = req.Nonce, Items = Wire.ToItems(req.Items), Signature = req.Signature.HexToByteArray(),
            }, ct);
            if (result is Microsoft.AspNetCore.Http.HttpResults.Ok<Response> ok)
            {
                guard.Relayed.Set(digest, ok.Value?.TransactionHash, new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(6) });
            }

            return result;
        });

        app.MapPost(Wire.SyncPath, async (Wire.SyncRequest req, HttpContext http, RelayerAccount relayer, VerifierAssignmentsResolver resolver, VerifierRelayGuard guard, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync() is not { } target)
            {
                return Results.NotFound();
            }

            if (!AddressUtil.Current.IsValidEthereumAddressHexFormat(req.Node))
            {
                return Results.BadRequest("Malformed node address.");
            }

            if (!guard.Allowed("ip:" + http.Connection.RemoteIpAddress))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            // Only a node whose pool membership is out of date costs a transaction (the call is permissionless on-chain too).
            var stale = await relayer.Web3.Eth.GetContractQueryHandler<StaleAmongFunction>()
                .QueryAsync<List<string>>(target.Address, new StaleAmongFunction { Nodes = [req.Node] });
            return stale.Count == 0
                ? Results.Ok(new Response(null, null))
                : await SendAsync(relayer, target.Address, new SyncManyFunction { Nodes = [req.Node] }, ct);
        });

        return app;
    }

    private static async Task<IResult> SendAsync<TFunction>(RelayerAccount relayer, string contract, TFunction function, CancellationToken ct)
        where TFunction : FunctionMessage, new()
    {
        try
        {
            var receipt = await relayer.SendAsync(contract, function, ct);
            if (receipt.Status.Value == 1)
            {
                return Results.Ok(new Response(receipt.TransactionHash, null));
            }

            var tx = await relayer.Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(receipt.TransactionHash);
            var error = GasHeadroom.RanOutOfGas(receipt.GasUsed.Value, tx?.Gas?.Value)
                ? $"Transaction {receipt.TransactionHash} used its whole gas limit, likely out of gas."
                : $"Transaction {receipt.TransactionHash} reverted.";
            return Results.Json(new Response(receipt.TransactionHash, error), statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex) when (ex is Nethereum.JsonRpc.Client.RpcResponseException or SmartContractCustomErrorRevertException)
        {
            var described = ex is SmartContractCustomErrorRevertException c ? $"{c.Message} {c.ExceptionEncodedData}"
                : $"{ex.Message} {((Nethereum.JsonRpc.Client.RpcResponseException)ex).RpcError?.Data}";
            var terminal = Terminal.FirstOrDefault(s => described.Contains(Selector(s), StringComparison.OrdinalIgnoreCase));
            return terminal is not null
                ? Results.Json(new Response(null, terminal[..terminal.IndexOf('(')]), statusCode: StatusCodes.Status409Conflict)
                : Results.Json(new Response(null, described.Trim()), statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static string Selector(string signature) =>
        Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes(signature))[..4].ToHex(false);
}

/// <summary>The relay's guards (#227): per-key rate limits (each Verifier and each IP, 20 a minute) and batches already relayed.</summary>
public sealed class VerifierRelayGuard : IDisposable
{
    private readonly System.Threading.RateLimiting.PartitionedRateLimiter<string> _limits =
        System.Threading.RateLimiting.PartitionedRateLimiter.Create<string, string>(key =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(key, _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
            }));

    public MemoryCache Relayed { get; } = new(new MemoryCacheOptions { SizeLimit = 50_000 });

    public bool Allowed(string key)
    {
        using var lease = _limits.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _limits.Dispose();
        Relayed.Dispose();
    }
}
