using System.Numerics;
using Nethereum.Contracts;
using Nethereum.Contracts.ContractHandlers;

namespace Symposia.Blockchain.Contracts;

/// <summary>
/// The gas limit every Symposia .NET write sends (#216). eth_estimateGas runs against the latest block, but gas can
/// grow before the transaction is included: an ERC20Votes checkpoint under a new clock key needs a fresh storage slot,
/// and earlier sends still pending aren't in the estimate. Sending the bare estimate runs those out of gas. Only gas
/// used is charged, so the headroom costs nothing.
/// </summary>
public static class GasHeadroom
{
    /// <summary>Headroom is estimate / <see cref="Divisor"/> (20%) plus <see cref="Fixed"/>.</summary>
    public const int Divisor = 5;

    /// <summary>Covers one fresh storage slot (about 22k) and change.</summary>
    public const int Fixed = 30_000;

    /// <summary>estimate + 20% + <see cref="Fixed"/>.</summary>
    public static BigInteger Over(BigInteger estimate) => estimate + estimate / Divisor + Fixed;

    /// <summary>
    /// Whether a failed transaction ran out of gas rather than reverting (#219): a top-level out-of-gas consumes the
    /// whole limit, a revert returns what's left.
    /// </summary>
    public static bool RanOutOfGas(BigInteger gasUsed, BigInteger? gasLimit) => gasLimit is { } limit && gasUsed >= limit;

    /// <summary>
    /// Runs <paramref name="send"/> with <paramref name="function"/>'s gas set to the headroom over a fresh estimate,
    /// unless the caller set one, and restores the caller's value afterwards so a reused message is estimated afresh.
    /// </summary>
    public static async Task<T> WithHeadroomAsync<TFunction, T>(
        this IContractTransactionHandler<TFunction> handler, string contract, TFunction function, Func<Task<T>> send)
        where TFunction : FunctionMessage, new()
    {
        var callerGas = function.Gas;
        try
        {
            function.Gas ??= Over((await handler.EstimateGasAsync(contract, function)).Value);
            return await send();
        }
        finally
        {
            function.Gas = callerGas;
        }
    }
}
