using Grpc.Core;
using Symposia.BlobStorage.Domain;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Protocol;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Routing;

public enum Attestation
{
    /// <summary>The node proved the CID against the root the chain holds for it.</summary>
    Attested,

    /// <summary>The node has an on-chain root but couldn't prove the CID against it.</summary>
    NotAttested,

    /// <summary>Nothing to judge by: no chain, no registry identity, no root submitted yet, or a node without the RPC.</summary>
    Unverifiable,
}

/// <summary>
/// Layer 3 checks against nodes' on-chain epoch roots (#12 FR3.2, FR3.4, AC6). A node proves it holds a CID with
/// a Merkle inclusion proof; the proof is checked against the root the chain holds for that node and epoch, never
/// against the root the node itself sends. In a dispute between gossip and the chain the chain wins: a node that
/// can't prove a holding its feed claims is evicted from the routing cache and reported for that epoch's
/// penalty and reward calculation.
/// </summary>
/// <remarks>
/// Roots lag by up to an epoch (FR3.3), so a blob written this epoch is legitimately unattested; only an explicit
/// dispute evicts on <see cref="Attestation.NotAttested"/>. Lookups use it only to find extra holders.
/// </remarks>
public sealed class EpochRootDisputes(
    RoutingCache cache,
    Penalties.IIntegrityFailureSink failures,
    Health.GatewayMetrics metrics,
    ILogger<EpochRootDisputes> logger,
    IEpochRootClient? chain = null)
{
    /// <summary>Whether the chain can be consulted at all.</summary>
    public bool Enabled => chain is not null;

    /// <summary>Asks <paramref name="node"/> to prove it holds <paramref name="cid"/> in its latest on-chain root.</summary>
    public async Task<(Attestation Outcome, ulong Epoch)> CheckAsync(NodeConnection node, string cid, CancellationToken ct)
    {
        try
        {
            return await CheckOrThrowAsync(node, cid, ct);
        }
        catch (HttpRequestException ex)
        {
            // The Blockchain Gateway is unreachable: no verdict either way.
            logger.LogDebug("Couldn't read epoch roots for {Node}: {Message}", node.Id, ex.Message);
            return (Attestation.Unverifiable, 0);
        }
    }

    private async Task<(Attestation Outcome, ulong Epoch)> CheckOrThrowAsync(NodeConnection node, string cid, CancellationToken ct)
    {
        if (chain is null || node.ExpectedNodeId is not { } address)
        {
            return (Attestation.Unverifiable, 0);
        }

        ManifestProofResponse proof;
        try
        {
            proof = await node.Client.GetManifestProofAsync(new ManifestProofRequest { Cid = cid }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);
        }
        catch (RpcException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Node {Node} couldn't answer a manifest proof for {Cid}: {Status}.", node.Id, cid, ex.StatusCode);
            return (Attestation.Unverifiable, 0);
        }

        if (proof.Root.Length == 0)
        {
            // The node names no submitted epoch. If the chain has none from it either, there's nothing to judge by.
            return (await chain.GetLatestAsync(address, ct) is null ? Attestation.Unverifiable : Attestation.NotAttested, proof.Epoch);
        }

        // The root that counts is the chain's; the one the node sends only says which epoch it means.
        if (await chain.GetRootAsync(address, proof.Epoch, ct) is not { } onChain)
        {
            // An epoch the chain doesn't hold: against the node only if the chain holds any root from it at all.
            return (await chain.GetLatestAsync(address, ct) is null ? Attestation.Unverifiable : Attestation.NotAttested, proof.Epoch);
        }

        var proven = proof.Found && Cid.TryParse(cid, out var parsed) &&
            ManifestMerkle.Verify(new ManifestLeaf(parsed, proof.SizeBytes, proof.TenantId), proof.LeafIndex, proof.LeafCount,
                [.. proof.Siblings.Select(s => s.ToByteArray())], onChain);
        return (proven ? Attestation.Attested : Attestation.NotAttested, proof.Epoch);
    }

    /// <summary>
    /// Resolves a dispute over whether <paramref name="node"/> holds <paramref name="cid"/> (FR3.4): the on-chain
    /// root decides. Against the node, its routing entry is evicted and the incident reported.
    /// </summary>
    public async Task<Attestation> ResolveAsync(NodeConnection node, string cid, CancellationToken ct)
    {
        var (outcome, epoch) = await CheckAsync(node, cid, ct);
        metrics.Disputes.WithLabels(outcome.ToString()).Inc();
        switch (outcome)
        {
            case Attestation.NotAttested:
                cache.Evict(node.Id, cid);
                failures.Report(node.Id, cid, $"no inclusion proof against its on-chain manifest root for epoch {epoch}");
                logger.LogWarning("Dispute over {Cid} on {Node} resolved against the node (epoch {Epoch}); routing entry evicted.", cid, node.Id, epoch);
                break;
            case Attestation.Attested:
                cache.Learn(node.Id, cid);
                break;
        }

        return outcome;
    }
}
