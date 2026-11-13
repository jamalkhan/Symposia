using System.Numerics;
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
/// (the Verifier stops), 502 for failed receipts or transport (it retries).
/// </summary>
public static class VerifierRelayEndpoints
{
    private static readonly string[] Terminal =
    [
        "NotAssigned(uint256,address,uint8,address)", "WindowClosed(uint256)", "NotSeeded(uint256)", "InvalidSignature()",
        "AlreadyFinalizing(uint256)", "BadValues(uint256,uint256)",
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Assignments, ulong ChainId)> Resolved = new();

    public static IEndpointRouteBuilder MapVerifierRelay(this IEndpointRouteBuilder app)
    {
        app.MapPost(Wire.Path, async (Wire.Request req, RelayerAccount relayer, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            if (await ResolveAsync(relayer, options.Value.RewardDistributorAddress) is not { } target)
            {
                return Results.NotFound();
            }

            if (!AddressUtil.Current.IsValidEthereumAddressHexFormat(req.Verifier) || req.Items is not { Count: > 0 and <= Wire.MaxItems }
                || req.Items.Any(i => !AddressUtil.Current.IsValidEthereumAddressHexFormat(i.Target) || i.Values is not { Count: > 0 and <= 4 }
                    || i.Values.Any(v => !BigInteger.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
                || !req.Signature.IsHex() || req.Signature.HexToByteArray().Length != 65)
            {
                return Results.BadRequest("Malformed attestation batch.");
            }

            if (!string.Equals(Wire.Recover(target.Assignments, target.ChainId, req), req.Verifier, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new Response(null, "InvalidSignature"), statusCode: StatusCodes.Status409Conflict);
            }

            return await SendAsync(relayer, target.Assignments, new AttestFunction
            {
                Epoch = req.Epoch, Verifier = req.Verifier, Items = Wire.ToItems(req.Items), Signature = req.Signature.HexToByteArray(),
            }, ct);
        });

        app.MapPost(Wire.SyncPath, async (Wire.SyncRequest req, RelayerAccount relayer, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            if (await ResolveAsync(relayer, options.Value.RewardDistributorAddress) is not { } target)
            {
                return Results.NotFound();
            }

            // Permissionless on-chain too: it only makes the node's pool membership match its registry status.
            return AddressUtil.Current.IsValidEthereumAddressHexFormat(req.Node)
                ? await SendAsync(relayer, target.Assignments, new SyncManyFunction { Nodes = [req.Node] }, ct)
                : Results.BadRequest("Malformed node address.");
        });

        return app;
    }

    private static async Task<(string Assignments, ulong ChainId)?> ResolveAsync(RelayerAccount relayer, string distributor)
    {
        if (string.IsNullOrEmpty(distributor))
        {
            return null;
        }

        if (Resolved.TryGetValue(distributor, out var known))
        {
            return known;
        }

        var config = await relayer.Web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(distributor, new ConfigFunction());
        var assignments = await relayer.Web3.Eth.GetContractQueryHandler<GetAddressFunction>().QueryAsync<string>(config, new GetAddressFunction { Key = ConfigKeys.VerifierAssignments });
        if (string.IsNullOrEmpty(assignments) || assignments == "0x0000000000000000000000000000000000000000")
        {
            return null;
        }

        var resolved = (assignments, (ulong)(await relayer.Web3.Eth.ChainId.SendRequestAsync()).Value);
        Resolved[distributor] = resolved;
        return resolved;
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
