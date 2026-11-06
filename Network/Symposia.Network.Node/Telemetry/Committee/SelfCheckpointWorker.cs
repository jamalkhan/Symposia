using Microsoft.Extensions.Logging;
using Nethereum.Hex.HexConvertors.Extensions;

namespace Symposia.Network.Node.Telemetry.Committee;

/// <summary>
/// The liveness fallback every telemetry node runs (issue #158 FR3). Once a window's
/// self-checkpoint grace has passed with no committee checkpoint, the node commits a batch of
/// only its own samples for that window, through the Gateway relay. The relay also stores the
/// batch, so the node needs no blob credential. Windows the committee already committed are
/// never touched, and the contract enforces that too.
/// </summary>
public sealed class SelfCheckpointWorker(
    ITelemetryChain chain,
    ITelemetryRelay relay,
    InMemoryTelemetryGossip store,
    ITelemetrySigner signer,
    TelemetryEip712 eip712,
    ulong chainId,
    string registry,
    ILogger<SelfCheckpointWorker> logger)
{
    private readonly HashSet<(ulong, ushort)> _done = [];

    public int SelfCheckpoints { get; private set; }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var schedule = await chain.ScheduleAsync(ct);
        var now = await chain.NowAsync(ct);
        var grace = await chain.SelfCheckpointGraceAsync(ct);
        var current = schedule.EpochAt(now);
        foreach (var epoch in current == 0 ? [0UL] : new[] { current - 1, current })
        {
            if (now > await chain.TelemetryDeadlineAsync(epoch, ct))
            {
                continue;
            }

            for (ushort k = 0; k < schedule.CheckpointsPerEpoch; k++)
            {
                if (now < CommitteeWorker.WindowEnd(schedule, epoch, k) + grace || _done.Contains((epoch, k)))
                {
                    continue;
                }

                var mine = store.Heartbeats
                    .Where(h => string.Equals(h.Payload.NodeId, signer.Address, StringComparison.OrdinalIgnoreCase)
                                && h.Payload.EpochIndex == epoch && h.Payload.SampleIndex < schedule.SlotsPerEpoch
                                && schedule.CheckpointOf(h.Payload.SampleIndex) == k)
                    .ToList();
                if (mine.Count == 0
                    || await chain.CheckpointAsync(epoch, k, ct) is not null
                    || await chain.SelfCheckpointAsync(epoch, k, signer.Address, ct) is not null)
                {
                    _done.Add((epoch, k));
                    continue;
                }

                var bytes = TelemetryBatchCodec.Encode(epoch, k, mine, []);
                var address = TelemetryBatchCodec.Address(bytes);
                var root = TelemetryBatchCodec.Decode(bytes, schedule, chainId, registry).MerkleRoot;
                var signature = signer.Sign(eip712.SelfCheckpointDigest(signer.Address, epoch, k, root, address, (uint)mine.Count)).ToHex(true);
                _done.Add((epoch, k)); // one attempt per window: a rejection (e.g. node not Active) won't fix itself by retrying
                try
                {
                    await relay.SelfCheckpointAsync(signer.Address, epoch, k, root, address, (uint)mine.Count, signature, bytes, ct);
                    SelfCheckpoints++;
                    logger.LogWarning("Committee missed window {Epoch}/{Window}; self-checkpointed {Count} samples.", epoch, k, mine.Count);
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning("Committee missed window {Epoch}/{Window} and the self-checkpoint was rejected: {Reason}", epoch, k, ex.Message);
                }
            }
        }

        _done.RemoveWhere(d => d.Item1 + 1 < current);
    }
}
