using Symposia.Identity.Gateway.Chain;

namespace Symposia.Identity.Gateway.Endpoints;

/// <summary>The one place a chain write's outcome becomes an HTTP response (#222).</summary>
public static class ChainResults
{
    public static IResult ToResult(ChainCallException ex, HttpContext http)
    {
        var code = EthereumChainClient.Code(ex.Kind);
        switch (ex.Kind)
        {
            case ChainFailureKind.Pending:
                // Not a failure: the write may still land. Resolve it at the status endpoint; never resubmit.
                return Results.Accepted($"/v1/transactions/{ex.TxHash}", new { status = code, txHash = ex.TxHash });
            case ChainFailureKind.ChainUnavailable:
                http.Response.Headers.RetryAfter = "5";
                return Results.Json(new { error = ex.Message, code, txHash = ex.TxHash }, statusCode: StatusCodes.Status503ServiceUnavailable);
            case ChainFailureKind.OutOfGas:
                return Results.Json(new { error = ex.Message, code, txHash = ex.TxHash }, statusCode: StatusCodes.Status502BadGateway);
            default:
                return Results.Json(new { error = ex.Message, code, txHash = ex.TxHash }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
