using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.Contracts;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;
using Symposia.Blockchain.Contracts;
using Symposia.Blockchain.Gateway.Chain;
using Symposia.Network.Node.Rewards;
using Wire = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire;

namespace Symposia.Blockchain.Gateway.Rewards;

/// <summary>
/// Relays signed per-service factor reports to <c>RewardDistributor.submitServiceFactorMetrics</c> (#221), paying
/// gas from the relayer with <see cref="GasHeadroom"/>. The contract stays the authority: a bad signature, a wrong
/// factor count or a closed window reverts at estimation, before anything is spent. Answers 409 for those
/// terminal reverts (the reporter stops), 502 for a failed or out-of-gas receipt (the reporter retries). Before
/// spending anything it checks the signature against <c>reward.metric.signer</c> off-chain, and answers 200
/// without sending when the epoch already has a report, so replaying a valid report can't burn relayer gas.
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

            var report = new MetricReport(distributor, req.Node, req.Service, req.Epoch, values, req.HeartbeatBps);
            try
            {
                if (!string.Equals(MetricReportDigest.Recover(report, req.Signature), await MetricSignerAsync(relayer, distributor), StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new Wire.Response(null, "InvalidSignature"), statusCode: StatusCodes.Status409Conflict);
                }

                if (await SubmittedAsync(relayer, distributor, req.Node, req.Service, req.Epoch))
                {
                    return Results.Ok(new Wire.Response(null, null));
                }
            }
            catch (Exception ex) when (ex is Nethereum.JsonRpc.Client.RpcResponseException or HttpRequestException)
            {
                return Results.Json(new Wire.Response(null, ex.Message), statusCode: StatusCodes.Status502BadGateway);
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

    private static readonly byte[] MetricSignerKey = Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("reward.metric.signer"));

    private static (string Distributor, string Signer, DateTimeOffset At)? _signer;

    /// <summary>The configured metric signer, re-read every 5 minutes so a timelocked rotation is picked up.</summary>
    private static async Task<string> MetricSignerAsync(RelayerAccount relayer, string distributor)
    {
        if (_signer is { } c && c.Distributor == distributor && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(5))
        {
            return c.Signer;
        }

        var config = await relayer.Web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(distributor, new ConfigFunction());
        var signer = await relayer.Web3.Eth.GetContractQueryHandler<GetAddressFunction>().QueryAsync<string>(config, new GetAddressFunction { Key = MetricSignerKey });
        _signer = (distributor, signer, DateTimeOffset.UtcNow);
        return signer;
    }

    private static async Task<bool> SubmittedAsync(RelayerAccount relayer, string distributor, string node, byte service, ulong epoch)
    {
        var account = await relayer.Web3.Eth.GetContractQueryHandler<AccountOfFunction>()
            .QueryAsync<string>(distributor, new AccountOfFunction { Node = node, Service = service });
        var record = await relayer.Web3.Eth.GetContractQueryHandler<MetricOfFunction>()
            .QueryDeserializingToObjectAsync<MetricRecordOutput>(new MetricOfFunction { Epoch = epoch, Account = account }, distributor);
        return record.Submitted;
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
