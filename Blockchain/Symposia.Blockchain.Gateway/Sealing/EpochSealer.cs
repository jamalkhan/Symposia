using Microsoft.Extensions.Options;

namespace Symposia.Blockchain.Gateway.Sealing;

/// <summary>
/// Settles every epoch the distributor will accept, oldest first (issue #72; #76 adds the
/// telemetry gate). The distributor requires every node that reported, plus every node with a
/// telemetry record when it settles from telemetry, so the sealer gathers both from events and
/// cross-checks each against its on-chain count before sending. If either disagrees (for example
/// an RPC that trims logs), it stops and retries next pass rather than submitting a set the
/// contract would reject.
/// </summary>
public sealed class EpochSealer(IEpochSealChain chain, IOptions<GatewayOptions> options, ILogger<EpochSealer> logger)
{
    private readonly int _maxPerPass = Math.Max(1, options.Value.EpochSealer.MaxEpochsPerPass);
    private ulong? _waitLoggedFor;

    /// <summary>Runs one pass and returns how many epochs this gateway settled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var sealedCount = 0;
        try
        {
            await chain.PrepareVerifierEpochAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Couldn't seed this epoch's Verifier assignments; retrying next pass: {Reason}", ex.Message);
        }

        while (sealedCount < _maxPerPass && !ct.IsCancellationRequested)
        {
            var epoch = await chain.NextSealableEpochAsync(ct);
            if (!await chain.CanSealAsync(epoch, ct))
            {
                // Once per epoch, so a stalled pipeline is never silent (#181 FR5).
                if (_waitLoggedFor != epoch)
                {
                    logger.LogInformation(
                        "Epoch {Epoch} can't be sealed yet: its finality window hasn't passed, or its telemetry records are still pending within the fallback wait.",
                        epoch);
                    _waitLoggedFor = epoch;
                }

                break;
            }

            var reporters = await chain.ReportersAsync(epoch, ct);
            var expected = await chain.ReporterCountAsync(epoch, ct);
            if ((ulong)reporters.Count != expected)
            {
                logger.LogWarning(
                    "Not sealing epoch {Epoch}: found {Found} reporting nodes in logs but the distributor counts {Expected}; retrying next pass.",
                    epoch, reporters.Count, expected);
                break;
            }

            var nodes = new HashSet<string>(reporters, StringComparer.OrdinalIgnoreCase);
            if (await chain.SettlesFromTelemetryAsync(epoch, ct))
            {
                var recordNodes = await chain.RecordNodesAsync(epoch, ct);
                var recordCount = await chain.RecordCountAsync(epoch, ct);
                if ((ulong)recordNodes.Count != recordCount)
                {
                    logger.LogWarning(
                        "Not sealing epoch {Epoch}: found {Found} telemetry records in logs but the registry counts {Expected}; retrying next pass.",
                        epoch, recordNodes.Count, recordCount);
                    break;
                }

                nodes.UnionWith(recordNodes);
            }

            var sortedNodes = nodes.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            try
            {
                await chain.FinalizeVerifiersAsync(epoch, ct); // #227: the seal waits for Verifier results
                await chain.SealAsync(epoch, sortedNodes, ct);
                sealedCount++;
                logger.LogInformation("Sealed epoch {Epoch} with {Count} nodes.", epoch, sortedNodes.Count);
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
