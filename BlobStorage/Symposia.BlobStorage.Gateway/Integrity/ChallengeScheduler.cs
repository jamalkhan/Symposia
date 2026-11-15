using Grpc.Core;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Penalties;
using Symposia.BlobStorage.Gateway.Repair;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Integrity;

public enum ChallengeResult
{
    Passed,

    /// <summary>Wrong proof, wrong hash or the node says it lacks the blob.</summary>
    Failed,

    /// <summary>No valid answer within the timeout: an integrity failure too (#8 FR6.4).</summary>
    TimedOut,

    /// <summary>The node couldn't be asked (connection error): not evidence either way.</summary>
    Unreachable,
}

/// <summary>
/// Challenge-response proof of storage (#8 FR6.4): a random replica of a random object is asked for a
/// random 64 KiB leaf and its Merkle inclusion proof, checked against the root recorded at write time.
/// Only the leaf crosses the wire. Challenges come at exponentially distributed intervals, so a node can't
/// predict them. A failure is recorded against the node, reported to the penalty system, and the replica
/// goes to P0 repair. Objects without a recorded root (older nodes) fall back to a whole-blob hash.
/// </summary>
public sealed class ChallengeScheduler(
    GatewayMetadataStore store, INodeRegistry nodes, RepairQueue queue, IIntegrityFailureSink failures,
    IOptions<GatewayOptions> options, ILogger<ChallengeScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mean = options.Value.Redundancy.ChallengeIntervalSeconds;
        if (mean <= 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Exponential gaps (a Poisson process): the next challenge is never predictable from the last.
            var gap = -Math.Log(1 - Random.Shared.NextDouble()) * mean;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(gap, mean * 10)), stoppingToken);
            try
            {
                await ChallengeRandomAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Challenge round failed.");
            }
        }
    }

    /// <summary>One challenge of a random replica of a random object; null when there was nothing to challenge.</summary>
    public async Task<ChallengeResult?> ChallengeRandomAsync(CancellationToken ct)
    {
        if (store.RandomObject() is not { } record)
        {
            return null;
        }

        var replicas = nodes.All.Where(n => n.IsHealthy && record.NodeIds.Contains(n.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (replicas.Count == 0)
        {
            return null;
        }

        var node = replicas[Random.Shared.Next(replicas.Count)];
        var result = await ChallengeAsync(node, record, TimeSpan.FromSeconds(Math.Max(1, options.Value.Redundancy.ChallengeTimeoutSeconds)), ct);
        Record(node, record, result);
        return result;
    }

    /// <summary>Counts a failed or unanswered challenge against the node and repairs the replica (#8 AC9).</summary>
    public void Record(NodeConnection node, ObjectRecord record, ChallengeResult result)
    {
        if (result is not (ChallengeResult.Failed or ChallengeResult.TimedOut))
        {
            return;
        }

        node.ChallengeFailures++;
        failures.Report(node.Id, record.Cid, result == ChallengeResult.TimedOut ? "no proof within the timeout" : "invalid proof");
        logger.LogWarning("Node {Node} failed a storage challenge for {Cid} ({Result}); {Failures} failure(s), recommended penalty stage {Stage}.",
            node.Id, record.Cid, result, node.ChallengeFailures, PenaltyAssessment.RecommendedStage(node.ChallengeFailures));
        queue.EnqueueCorruptReplica(record, node.Id, store, nodes);
    }

    /// <summary>Challenges one replica: a Merkle leaf proof when a root is recorded, else a whole-blob hash.</summary>
    public static async Task<ChallengeResult> ChallengeAsync(NodeConnection node, ObjectRecord record, TimeSpan timeout, CancellationToken ct)
    {
        var root = record.MerkleRoot is { Length: MerkleTree.HashSize * 2 } hex ? Convert.FromHexString(hex) : null;
        var leafCount = MerkleTree.LeafCount(record.SizeBytes);
        var leaf = Random.Shared.NextInt64(leafCount);
        try
        {
            var response = await node.Client.IntegrityChallengeAsync(
                root is null
                    ? new IntegrityChallengeRequest { Cid = record.Cid }
                    : new IntegrityChallengeRequest { Cid = record.Cid, MerkleProof = true, LeafIndex = leaf },
                deadline: DateTime.UtcNow + timeout, cancellationToken: ct);

            if (root is not null && response.ProofSupported)
            {
                return response.LeafCount == leafCount
                    && MerkleTree.Verify(response.LeafData.Span, leaf, leafCount, response.Siblings.Select(s => s.ToByteArray()).ToList(), root)
                    ? ChallengeResult.Passed : ChallengeResult.Failed;
            }

            if (root is not null)
            {
                // An older node ignored the proof request and hashed the whole blob: compare with the CID.
                return response.Sha256Hex.Equals(record.Cid, StringComparison.OrdinalIgnoreCase) ? ChallengeResult.Passed : ChallengeResult.Failed;
            }

            return response.Sha256Hex.Equals(record.Cid, StringComparison.OrdinalIgnoreCase) ? ChallengeResult.Passed : ChallengeResult.Failed;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return ChallengeResult.Failed;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            return ChallengeResult.TimedOut;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return ChallengeResult.Unreachable;
        }
    }
}
