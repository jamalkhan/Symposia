using System.Text.RegularExpressions;
using Symposia.Identity.Gateway.Chain;

namespace Symposia.Identity.Gateway.Endpoints;

public static partial class TransactionEndpoints
{
    public static void Map(WebApplication app)
    {
        // #222 FR4: where a relayed write stands, so a 202 is resolved by polling here rather than by resubmitting.
        app.MapGet("/v1/transactions/{txHash}", async (string txHash, IChainClient chain, CancellationToken ct) =>
        {
            if (!TxHash().IsMatch(txHash))
            {
                return Results.BadRequest(new { error = "txHash must be 0x followed by 64 hex characters" });
            }

            ChainTransactionStatus? status;
            try
            {
                status = await chain.GetTransactionStatusAsync(txHash, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return Results.Json(new { error = $"chain unavailable: {ex.Message}", code = "chain_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return status is null
                ? Results.NotFound(new { error = "unknown transaction" })
                : Results.Ok(new { status = status.Status, code = status.Code, tokenId = status.TokenId });
        });
    }

    [GeneratedRegex("^0x[0-9a-fA-F]{64}$")]
    private static partial Regex TxHash();
}
