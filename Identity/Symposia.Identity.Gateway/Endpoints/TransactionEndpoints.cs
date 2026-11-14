using System.Text.RegularExpressions;
using Symposia.Identity.Gateway.Chain;

namespace Symposia.Identity.Gateway.Endpoints;

public static partial class TransactionEndpoints
{
    public static void Map(WebApplication app)
    {
        // #222 FR4: where a relayed write stands, so a 202 is resolved by polling here rather than by resubmitting.
        app.MapGet("/v1/transactions/{txHash}", async (string txHash, IChainClient chain, HttpContext http) =>
        {
            if (!IsTxHash(txHash))
            {
                return Results.BadRequest(new { error = "txHash must be 0x followed by 64 hex characters" });
            }

            ChainTransactionStatus? status;
            try
            {
                status = await chain.GetTransactionStatusAsync(txHash, http.RequestAborted);
            }
            catch (Exception ex) when (EthereumChainClient.IsRpcFault(ex))
            {
                return ChainResults.Unavailable($"chain unavailable: {ex.Message}", txHash, http);
            }

            return status is null
                ? Results.NotFound(new { error = "unknown transaction" })
                : Results.Ok(new { status = status.State.ToString().ToLowerInvariant(), code = status.Failure?.ToWireName(), tokenId = status.TokenId });
        });
    }

    internal static bool IsTxHash(string value) => TxHash().IsMatch(value);

    [GeneratedRegex("^0x[0-9a-fA-F]{64}$")]
    private static partial Regex TxHash();
}
