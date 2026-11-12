using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.Contracts;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;
using Symposia.Blockchain.Contracts;
using Symposia.Blockchain.Gateway.Chain;
using Wire = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire;

namespace Symposia.Blockchain.Gateway.Rewards;

/// <summary>
/// Relays signed per-service factor reports to <c>RewardDistributor.submitServiceFactorMetrics</c> (#221), paying
/// gas from the relayer with <see cref="GasHeadroom"/>. The contract stays the authority: a bad signature, a wrong
/// factor count or a closed window reverts at estimation, before anything is spent. Answers 409 for those
/// terminal reverts (the reporter stops), 502 for a failed or out-of-gas receipt (the reporter retries).
/// </summary>
public static class ServiceMetricsRelayEndpoints
{
    private const int MaxFactors = 16;

    private static readonly string[] Terminal =
    [
        "EpochAlreadySealed(uint256)",
        "FinalityWindowElapsed(uint256)",
        "InvalidSignature()",
        "FactorCountMismatch(uint256,uint256)",
    ];

    public static IEndpointRouteBuilder MapServiceMetricsRelay(this IEndpointRouteBuilder app)
    {
        app.MapPost(Wire.Path, async (Wire.Request req, RelayerAccount relayer, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            var distributor = options.Value.RewardDistributorAddress;
            if (string.IsNullOrEmpty(distributor))
            {
                return Results.NotFound();
            }

            var values = new List<BigInteger>();
            foreach (var v in req.Values ?? [])
            {
                if (!BigInteger.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
                {
                    return Results.BadRequest("Factor values must be non-negative decimal integers.");
                }

                values.Add(n);
            }

            if (!AddressUtil.Current.IsValidEthereumAddressHexFormat(req.Node) || values.Count is 0 or > MaxFactors
                || req.HeartbeatBps > 10_000 || !req.Signature.IsHex() || req.Signature.HexToByteArray().Length != 65)
            {
                return Results.BadRequest("Malformed service metric report.");
            }

            var function = new SubmitServiceFactorMetricsFunction
            {
                Node = req.Node,
                Service = req.Service,
                Epoch = req.Epoch,
                Values = values,
                HeartbeatBps = req.HeartbeatBps,
                Signature = req.Signature.HexToByteArray(),
            };
            try
            {
                var receipt = await relayer.SendAsync(distributor, function, ct);
                if (receipt.Status.Value == 1)
                {
                    return Results.Ok(new Wire.Response(receipt.TransactionHash, null));
                }

                var tx = await relayer.Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(receipt.TransactionHash);
                var error = GasHeadroom.RanOutOfGas(receipt.GasUsed.Value, tx?.Gas?.Value)
                    ? $"Transaction {receipt.TransactionHash} used its whole gas limit ({receipt.GasUsed.Value} of {tx!.Gas.Value}), likely out of gas."
                    : $"Transaction {receipt.TransactionHash} reverted.";
                return Results.Json(new Wire.Response(receipt.TransactionHash, error), statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception ex) when (ex is Nethereum.JsonRpc.Client.RpcResponseException or SmartContractCustomErrorRevertException)
            {
                var described = Describe(ex);
                var terminal = Terminal.FirstOrDefault(s => described.Contains(Selector(s), StringComparison.OrdinalIgnoreCase));
                return terminal is not null
                    ? Results.Json(new Wire.Response(null, terminal[..terminal.IndexOf('(')]), statusCode: StatusCodes.Status409Conflict)
                    : Results.Json(new Wire.Response(null, described.Trim()), statusCode: StatusCodes.Status502BadGateway);
            }
        });

        return app;
    }

    /// <summary>Message plus any revert data, so custom-error selectors can be matched.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        SmartContractCustomErrorRevertException c => $"{c.Message} {c.ExceptionEncodedData}",
        Nethereum.JsonRpc.Client.RpcResponseException r => $"{r.Message} {r.RpcError?.Data}",
        _ => ex.Message,
    };

    private static string Selector(string signature) =>
        Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes(signature))[..4].ToHex(false);
}
