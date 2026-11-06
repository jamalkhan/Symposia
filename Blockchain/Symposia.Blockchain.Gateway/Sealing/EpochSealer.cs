using Microsoft.Extensions.Options;

namespace Symposia.Blockchain.Gateway.Sealing;

/// <summary>
/// Settles every epoch whose finality window has closed, oldest first (issue #72). The
/// distributor requires the full set of reporting nodes, so the sealer gathers them from report
/// events and cross-checks the count against the distributor's own tally before sending; if the
/// two disagree (for example an RPC that trims logs) it stops and retries next pass rather than
/// submitting a set the contract would reject.
/// </summary>
public sealed class EpochSealer(IEpochSealChain chain, IOptions<GatewayOptions> options, ILogger<EpochSealer> logger)
{
    private readonly int _maxPerPass = Math.Max(1, options.Value.EpochSealer.MaxEpochsPerPass);

    /// <summary>Runs one pass and returns how many epochs this gateway settled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var sealedCount = 0;
        while (sealedCount < _maxPerPass && !ct.IsCancellationRequested)
        {
            var epoch = await chain.NextSealableEpochAsync(ct);
            if (!await chain.IsEpochFinalAsync(epoch, ct))
            {
                break;
            }

            var reporters = (await chain.ReportersAsync(epoch, ct)).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            var expected = await chain.ReporterCountAsync(epoch, ct);
            if ((ulong)reporters.Count != expected)
            {
                logger.LogWarning(
                    "Not sealing epoch {Epoch}: found {Found} reporting nodes in logs but the distributor counts {Expected}; retrying next pass.",
                    epoch, reporters.Count, expected);
                break;
            }

            try
            {
                await chain.SealAsync(epoch, reporters, ct);
                sealedCount++;
                logger.LogInformation("Sealed epoch {Epoch} with {Count} reporting nodes.", epoch, reporters.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Another sealer may have won the race; that's success, not an error.
                if (await chain.NextSealableEpochAsync(ct) > epoch)
                {
                    logger.LogInformation("Epoch {Epoch} was sealed by another caller.", epoch);
                    continue;
                }

                throw;
            }
        }

        return sealedCount;
    }
}

/// <summary>Runs <see cref="EpochSealer"/> on a fixed poll; a failed pass is logged and retried.</summary>
public sealed class EpochSealerService(EpochSealer sealer, IOptions<GatewayOptions> options, ILogger<EpochSealerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.EpochSealer.PollSeconds));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await sealer.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Epoch sealing pass failed; retrying in {Interval}.", interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
