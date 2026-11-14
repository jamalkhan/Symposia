using Symposia.Identity.Gateway.Chain;

namespace Symposia.Identity.Gateway.Endpoints;

/// <summary>The one place a chain write's outcome becomes an HTTP response (#222).</summary>
public static class ChainResults
{
    public static IResult ToResult(ChainCallException ex, HttpContext http)
    {
        var code = ex.Kind.ToWireName();
        switch (ex.Kind)
        {
            case ChainFailureKind.Pending:
                // Not a failure: the write may still land. Resolve it at the status endpoint; never resubmit.
                return Results.Accepted($"/v1/transactions/{ex.TxHash}", new { status = code, txHash = ex.TxHash });
            case ChainFailureKind.ChainUnavailable:
                return Unavailable(ex.Message, ex.TxHash, http);
            case ChainFailureKind.OutOfGas or ChainFailureKind.Fault:
                return Results.Json(new { error = ex.Message, code, txHash = ex.TxHash }, statusCode: StatusCodes.Status502BadGateway);
            default:
                return Results.Json(new { error = ex.Message, code, txHash = ex.TxHash }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>503 with a back-off hint, for writes and the status endpoint alike.</summary>
    public static IResult Unavailable(string message, string? txHash, HttpContext http)
    {
        http.Response.Headers.RetryAfter = "5";
        return Results.Json(new { error = message, code = ChainFailureKind.ChainUnavailable.ToWireName(), txHash },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
