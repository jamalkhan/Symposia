using Microsoft.Extensions.Options;
using Symposia.Network.Node;
using Symposia.Network.Node.Telemetry;

namespace Symposia.BlobStorage.StorageNode.Storage;

/// <summary>
/// Attests this node's manifest on-chain once per epoch (#12 Layer 3, FR3.1, AC5). Shortly after each epoch
/// boundary it freezes the Active manifest as the finished epoch's holdings, computes the root
/// (<see cref="Domain.ManifestMerkle"/>), signs it with the node key and submits it through the Blockchain
/// Gateway to EpochRootRegistry. It reads only the local manifest: no gossip, no gateway (FR4.1).
/// </summary>
/// <remarks>
/// A root is only ever submitted during the epoch right after the one it attests. A node that was down for that
/// whole epoch is non-attesting for it; it never submits late, because a late root can't be trusted to reflect
/// that epoch's holdings (Arch policy for QA TC3.5; to be ratified by tokenomics). Frozen epochs are kept for the
/// last two, so proofs can be answered for the root the chain currently shows.
/// </remarks>
public sealed class EpochRootSubmitter(
    ManifestStore manifest,
    NodeIdentity identity,
    IEpochRootClient chain,
    IOptions<StorageNodeOptions> options,
    IOptions<NodeNetworkOptions> network,
    TimeProvider time,
    ILogger<EpochRootSubmitter> logger,
    TelemetryScheduleSource schedule) : BackgroundService
{
    private readonly EpochRootOptions _options = options.Value.EpochRoots;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Epoch numbers must be the chain's: never attest under a placeholder schedule (#181 FR1).
        await schedule.WhenSyncedAsync(stoppingToken);
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.CheckIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Epoch root submission failed; retrying in {Interval}.", interval);
            }

            await Task.Delay(interval, time, stoppingToken);
        }
    }

    /// <summary>
    /// One pass: freeze and submit the epoch that just ended, if it isn't on-chain yet. Returns the record of the
    /// epoch it worked on, or null at epoch 0 (nothing has ended yet). Public so tests can drive epochs directly.
    /// </summary>
    public async Task<EpochRootRecord?> RunOnceAsync(CancellationToken ct)
    {
        var current = schedule.Current.EpochAt(time.GetUtcNow().ToUnixTimeSeconds());
        if (current == 0)
        {
            return null;
        }

        var target = current - 1;
        var record = manifest.FreezeEpoch(target);
        manifest.PruneEpochs(target > 0 ? target - 1 : 0);
        if (record.SubmittedAt is not null || record.Outcome is "conflict")
        {
            return record;
        }

        var signature = identity.SignEpochRoot(target, record.Root, _options.RegistryAddress, network.Value.ChainId);
        var outcome = await chain.SubmitAsync(identity.NodeId, target, record.Root, signature, ct);
        switch (outcome)
        {
            case EpochRootSubmission.Submitted or EpochRootSubmission.AlreadyRecorded:
                manifest.MarkEpoch(target, outcome == EpochRootSubmission.Submitted ? "submitted" : "already-recorded", onChain: true);
                logger.LogInformation("Submitted the manifest root for epoch {Epoch} ({Leaves} leaves, root {Root}).", target, record.LeafCount,
                    Convert.ToHexStringLower(record.Root));
                break;
            case EpochRootSubmission.Conflict:
                // The chain never overwrites a root: this node's manifest for the epoch disagrees with what it signed before.
                manifest.MarkEpoch(target, "conflict", onChain: false);
                logger.LogError("The chain already holds a different root from this node for epoch {Epoch}; not resubmitting.", target);
                break;
            default:
                manifest.MarkEpoch(target, "rejected", onChain: false);
                logger.LogWarning("EpochRootRegistry refused this node's root for epoch {Epoch} (is the node registered?); will retry.", target);
                break;
        }

        // Marking it missed would never be read: a later pass works on the next epoch, so a lapsed one just stays unsubmitted.
        return manifest.GetEpochRoot(target);
    }
}
