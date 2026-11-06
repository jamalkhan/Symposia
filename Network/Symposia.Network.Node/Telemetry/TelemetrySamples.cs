using Nethereum.ABI;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node.Telemetry;

/// <summary>
/// A node's self-reported 5-minute heartbeat (issue #75 FR1). Units match the on-chain
/// <c>TelemetryCheckpointRegistry.MetricValues</c>: throughput KB/s, bandwidth kbit/s, latency
/// and retrieval ms. Storage-specific fields (<see cref="AvailableBytes"/>, <see cref="UsedBytes"/>)
/// feed tokenomics-mvp.md §6.3's factor table; other node types leave them zero.
/// </summary>
/// <param name="SampleIndex">Slot within the epoch: <c>(timestamp - epochStart) / cadence</c>. Part of the replay key.</param>
public sealed record HeartbeatSample(
    string NodeId,
    ulong EpochIndex,
    uint SampleIndex,
    long UnixSeconds,
    ulong ReadThroughputKBps,
    ulong WriteThroughputKBps,
    ulong BandwidthInKbps,
    ulong BandwidthOutKbps,
    uint LatencyMs,
    uint RetrievalMs,
    ulong AvailableBytes,
    ulong UsedBytes)
{
    /// <summary>Replay key (#75 arch): one sample per node per slot per epoch.</summary>
    public (string, ulong, uint) ReplayKey => (NodeId.ToLowerInvariant(), EpochIndex, SampleIndex);
}

/// <summary>A verifier's signed observation of <see cref="TargetNodeId"/> (issue #75 FR2).</summary>
public sealed record Attestation(
    string VerifierId,
    string TargetNodeId,
    ulong EpochIndex,
    uint SampleIndex,
    long UnixSeconds,
    bool Reachable,
    uint LatencyMs,
    uint RetrievalMs)
{
    public (string, string, ulong, uint) ReplayKey =>
        (VerifierId.ToLowerInvariant(), TargetNodeId.ToLowerInvariant(), EpochIndex, SampleIndex);
}

/// <summary>A payload plus its signer's EIP-191 signature over <see cref="TelemetrySigning"/>'s digest.</summary>
public sealed record SignedTelemetry<T>(T Payload, string Signature) where T : class;

/// <summary>
/// Canonical encoding and signing for telemetry (issue #75). Digests are keccak256 over the
/// ABI-packed fields, prefixed with a domain string and bound to the network (chain + registry)
/// so samples can't be replayed across networks. Signatures use the node key with EIP-191, the
/// same primitive as the #129 handshake. The Merkle leaf for a signed item is
/// <c>keccak256(digest || signature)</c>, so a checkpoint commits to the signature itself and
/// #79 challenges can prove exactly what the node signed.
/// </summary>
public static class TelemetrySigning
{
    public const string HeartbeatDomain = "Symposia.Heartbeat.v1";
    public const string AttestationDomain = "Symposia.Attestation.v1";

    private static readonly EthereumMessageSigner MessageSigner = new();

    public static byte[] Digest(HeartbeatSample s, ulong chainId, string registry) =>
        Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncodedPacked(
            new ABIValue("string", HeartbeatDomain),
            new ABIValue("uint256", chainId),
            new ABIValue("address", registry),
            new ABIValue("address", s.NodeId),
            new ABIValue("uint64", s.EpochIndex),
            new ABIValue("uint32", s.SampleIndex),
            new ABIValue("uint64", (ulong)s.UnixSeconds),
            new ABIValue("uint64", s.ReadThroughputKBps),
            new ABIValue("uint64", s.WriteThroughputKBps),
            new ABIValue("uint64", s.BandwidthInKbps),
            new ABIValue("uint64", s.BandwidthOutKbps),
            new ABIValue("uint32", s.LatencyMs),
            new ABIValue("uint32", s.RetrievalMs),
            new ABIValue("uint64", s.AvailableBytes),
            new ABIValue("uint64", s.UsedBytes)));

    public static byte[] Digest(Attestation a, ulong chainId, string registry) =>
        Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncodedPacked(
            new ABIValue("string", AttestationDomain),
            new ABIValue("uint256", chainId),
            new ABIValue("address", registry),
            new ABIValue("address", a.VerifierId),
            new ABIValue("address", a.TargetNodeId),
            new ABIValue("uint64", a.EpochIndex),
            new ABIValue("uint32", a.SampleIndex),
            new ABIValue("uint64", (ulong)a.UnixSeconds),
            new ABIValue("bool", a.Reachable),
            new ABIValue("uint32", a.LatencyMs),
            new ABIValue("uint32", a.RetrievalMs)));

    public static SignedTelemetry<HeartbeatSample> Sign(EthECKey key, HeartbeatSample s, ulong chainId, string registry) =>
        new(s, MessageSigner.Sign(Digest(s, chainId, registry), key));

    public static SignedTelemetry<Attestation> Sign(EthECKey key, Attestation a, ulong chainId, string registry) =>
        new(a, MessageSigner.Sign(Digest(a, chainId, registry), key));

    /// <summary>True if the signature recovers to the sample's claimed node.</summary>
    public static bool Verify(SignedTelemetry<HeartbeatSample> signed, ulong chainId, string registry) =>
        Recovers(Digest(signed.Payload, chainId, registry), signed.Signature, signed.Payload.NodeId);

    /// <summary>True if the signature recovers to the attestation's claimed verifier.</summary>
    public static bool Verify(SignedTelemetry<Attestation> signed, ulong chainId, string registry) =>
        Recovers(Digest(signed.Payload, chainId, registry), signed.Signature, signed.Payload.VerifierId);

    public static byte[] Leaf(byte[] digest, string signature) =>
        Sha3Keccack.Current.CalculateHash(digest.Concat(signature.HexToByteArray()).ToArray());

    private static bool Recovers(byte[] digest, string signature, string claimed)
    {
        try
        {
            var signer = MessageSigner.EcRecover(digest, signature);
            return string.Equals(signer, claimed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return false;
        }
    }
}
